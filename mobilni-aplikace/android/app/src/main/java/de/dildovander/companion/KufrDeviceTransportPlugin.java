package de.dildovander.companion;

import android.app.AlertDialog;
import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import android.widget.EditText;
import android.text.InputType;

import androidx.annotation.NonNull;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;
import java.util.Arrays;
import java.util.Locale;
import java.util.concurrent.TimeUnit;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.X509TrustManager;

import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;

@CapacitorPlugin(name = "KufrDeviceTransport")
public final class KufrDeviceTransportPlugin extends Plugin {
    private static final MediaType JSON = MediaType.get("application/json; charset=utf-8");
    private static final int MAX_FRAME_BYTES = 8192;
    private final Object lock = new Object();
    private WebSocket socket;
    private String connectionId;
    private String selectedHost;

    @PluginMethod
    public void savedDevice(PluginCall call) {
        try {
            String json = IdentityStore.read(getContext());
            if (json == null) {
                call.resolve(new JSObject().put("paired", false));
                return;
            }
            org.json.JSONObject identity = new org.json.JSONObject(json);
            JSObject result = new JSObject();
            result.put("paired", true);
            result.put("deviceId", identity.getString("deviceId"));
            result.put("host", selectedHost == null ? identity.getString("host") : selectedHost);
            call.resolve(result);
        } catch (Exception exception) {
            call.reject("Saved pairing is unavailable. Unlock the device and try again.", "credential_unavailable");
        }
    }

    @PluginMethod
    public void discover(PluginCall call) {
        // The ESP32 discovery protocol is Bonjour on iOS. Until an Android
        // discovery implementation is available, provide the same explicit
        // empty result shape and allow a manually entered local address.
        JSObject result = new JSObject();
        result.put("failed", false);
        result.put("bleAddressAvailable", false);
        result.put("devices", new org.json.JSONArray());
        call.resolve(result);
    }

    @PluginMethod
    public void setAddress(PluginCall call) {
        String host = call.getString("host", "");
        if (!validHost(host)) {
            call.reject("Enter a local IP address or DNS name without a port.", "invalid_address");
            return;
        }
        try {
            org.json.JSONObject identity = readIdentity();
            identity.put("host", host);
            IdentityStore.write(getContext(), identity.toString());
            selectedHost = null;
            call.resolve();
        } catch (Exception exception) {
            call.reject("Pair the device before changing its address.", "not_paired");
        }
    }

    @PluginMethod
    public void pair(PluginCall call) {
        String host = call.getString("host", "");
        String deviceId = call.getString("deviceId", "");
        String pin = call.getString("spkiSha256", "").toLowerCase(Locale.ROOT);
        if (!validHost(host) || !validDeviceId(deviceId) || !validHex(pin, 32)) {
            call.reject("Check the local address and QR pairing details.", "invalid_identity");
            return;
        }
        try {
            if (IdentityStore.read(getContext()) != null) {
                call.reject("A device is already paired. Forget it before pairing another.", "already_paired");
                return;
            }
        } catch (Exception exception) {
            call.reject("Secure pairing storage is unavailable.", "credential_unavailable");
            return;
        }

        EditText secretField = new EditText(getActivity());
        secretField.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        secretField.setSingleLine(true);
        AlertDialog dialog = new AlertDialog.Builder(getActivity())
            .setTitle("Pair Wand Night Stand")
            .setMessage("Enter the one-time secret from the QR code inside the case.")
            .setView(secretField)
            .setNegativeButton("Cancel", (ignored, which) -> call.reject("Pairing was cancelled.", "cancelled"))
            .setPositiveButton("Pair", null)
            .create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            String secret = secretField.getText().toString();
            secretField.setText("");
            if (!validHex(secret, -1) || secret.length() < 32 || secret.length() > 128 || secret.length() % 2 != 0) {
                secretField.setError("Enter the pairing secret as hexadecimal.");
                return;
            }
            dialog.dismiss();
            pairAsync(call, host, deviceId, pin, secret);
        }));
        dialog.show();
    }

    private void pairAsync(PluginCall call, String host, String deviceId, String pin, String secret) {
        try {
            OkHttpClient client = pinnedClient(host, pin);
            String body = new org.json.JSONObject().put("protocolVersion", 1).put("pairingSecret", secret).toString();
            Arrays.fill(secret.toCharArray(), '\0');
            Request request = new Request.Builder().url("https://" + host + "/v1/pair")
                .post(RequestBody.create(body, JSON)).build();
            client.newCall(request).enqueue(new Callback() {
                @Override public void onFailure(@NonNull Call ignored, @NonNull IOException exception) {
                    call.reject("Pairing failed. Check the local network and QR details.", "pairing_failed");
                }
                @Override public void onResponse(@NonNull Call ignored, @NonNull Response response) {
                    try (response) {
                        if (response.code() != 200 || response.body() == null || response.body().contentLength() > 192) {
                            call.reject("Pairing failed. Check the local network and QR details.", "pairing_failed");
                            return;
                        }
                        byte[] responseBytes = response.body().bytes();
                        if (responseBytes.length > 192) {
                            call.reject("Pairing response was invalid.", "pairing_failed");
                            return;
                        }
                        org.json.JSONObject result = new org.json.JSONObject(new String(responseBytes, StandardCharsets.UTF_8));
                        String token = result.getString("token");
                        if (result.length() != 2 || result.getInt("protocolVersion") != 1 || !validHex(token, 32)) {
                            call.reject("Pairing response was invalid.", "pairing_failed");
                            return;
                        }
                        org.json.JSONObject identity = new org.json.JSONObject()
                            .put("deviceId", deviceId).put("host", host).put("spkiSha256", pin).put("token", token);
                        IdentityStore.write(getContext(), identity.toString());
                        JSObject paired = new JSObject();
                        paired.put("deviceId", deviceId);
                        paired.put("host", host);
                        call.resolve(paired);
                    } catch (Exception exception) {
                        call.reject("Pairing could not be saved securely.", "credential_write_failed");
                    }
                }
            });
        } catch (Exception exception) {
            call.reject("Secure connection could not be established.", "tls_failed");
        }
    }

    @PluginMethod
    public void connect(PluginCall call) {
        String id = call.getString("connectionId", "");
        if (!id.matches("(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")) {
            call.reject("Connection is unavailable.", "connection_unavailable");
            return;
        }
        try {
            org.json.JSONObject identity = readIdentity();
            String deviceId = identity.getString("deviceId");
            String capturedDeviceId = call.getString("deviceId", deviceId);
            String host = call.getString("host", selectedHost == null ? identity.getString("host") : selectedHost);
            if (!deviceId.equals(capturedDeviceId) || !validHost(host)) {
                call.reject("Saved device identity changed. Pair it again.", "invalid_identity");
                return;
            }
            WebSocket newSocket = pinnedClient(host, identity.getString("spkiSha256")).newWebSocket(
                new Request.Builder().url("wss://" + host + "/v1/control")
                    .header("Authorization", "Bearer " + identity.getString("token")).build(),
                new WebSocketListener() {
                    @Override public void onOpen(@NonNull WebSocket webSocket, @NonNull Response response) {
                        synchronized (lock) {
                            if (socket != webSocket || !id.equals(connectionId)) { webSocket.close(1000, null); return; }
                        }
                        notifyTransport(id, "open", null, null);
                    }
                    @Override public void onMessage(@NonNull WebSocket webSocket, @NonNull String text) {
                        if (text.getBytes(StandardCharsets.UTF_8).length > MAX_FRAME_BYTES) {
                            closeConnection(webSocket, "protocol_error");
                            return;
                        }
                        synchronized (lock) { if (socket != webSocket || !id.equals(connectionId)) return; }
                        notifyTransport(id, "frame", text, null);
                    }
                    @Override public void onFailure(@NonNull WebSocket webSocket, @NonNull Throwable error, Response response) {
                        closeConnection(webSocket, classifyFailure(response == null ? 0 : response.code(), error));
                    }
                    @Override public void onClosed(@NonNull WebSocket webSocket, int code, @NonNull String reason) {
                        closeConnection(webSocket, code == 1009 ? "protocol_error" : "connection_lost");
                    }
                });
            synchronized (lock) {
                if (socket != null) socket.close(1000, null);
                socket = newSocket;
                connectionId = id;
            }
            call.resolve();
        } catch (Exception exception) {
            call.reject("Saved pairing is unavailable.", "credential_unavailable");
        }
    }

    @PluginMethod
    public void send(PluginCall call) {
        String id = call.getString("connectionId", "");
        String frame = call.getString("frame", "");
        WebSocket active;
        synchronized (lock) {
            if (socket == null || !id.equals(connectionId) || frame.getBytes(StandardCharsets.UTF_8).length > MAX_FRAME_BYTES) {
                call.reject("Connection changed; frame was not sent.", "wrong_connection");
                return;
            }
            active = socket;
        }
        if (active.send(frame)) call.resolve();
        else call.reject("Frame could not be sent.", "send_failed");
    }

    @PluginMethod
    public void close(PluginCall call) {
        String id = call.getString("connectionId", "");
        synchronized (lock) {
            if (socket != null && id.equals(connectionId)) closeConnection(socket, null);
        }
        call.resolve();
    }

    @PluginMethod
    public void forget(PluginCall call) {
        synchronized (lock) {
            if (socket != null) closeConnection(socket, null);
        }
        try {
            IdentityStore.clear(getContext());
            selectedHost = null;
            call.resolve();
        } catch (Exception exception) {
            call.reject("Pairing could not be removed.", "credential_write_failed");
        }
    }

    private void closeConnection(WebSocket target, String failure) {
        String id;
        synchronized (lock) {
            if (socket != target) return;
            id = connectionId;
            socket = null;
            connectionId = null;
        }
        target.cancel();
        if (failure != null && id != null) notifyTransport(id, "closed", null, failure);
    }

    private void notifyTransport(String id, String state, String frame, String failure) {
        JSObject event = new JSObject();
        event.put("connectionId", id);
        event.put("state", state);
        if (frame != null) event.put("frame", frame);
        if (failure != null) event.put("failureCode", failure);
        notifyListeners("transport", event);
    }

    private static String classifyFailure(int status, Throwable error) {
        if (status == 401 || status == 403) return "authorization_rejected";
        if (status == 409 || status == 503) return "server_busy";
        if (status > 0) return "handshake_rejected";
        if (error instanceof javax.net.ssl.SSLPeerUnverifiedException) return "certificate_mismatch";
        if (error instanceof java.net.SocketTimeoutException) return "connection_timeout";
        if (error instanceof java.net.UnknownHostException || error instanceof java.net.ConnectException) return "host_unreachable";
        return "connection_lost";
    }

    private static OkHttpClient pinnedClient(String host, String expectedPin) throws Exception {
        X509TrustManager trustManager = new X509TrustManager() {
            @Override public void checkClientTrusted(X509Certificate[] chain, String authType) throws java.security.cert.CertificateException {
                throw new java.security.cert.CertificateException("Client certificates are unsupported");
            }
            @Override public void checkServerTrusted(X509Certificate[] chain, String authType) throws java.security.cert.CertificateException {
                if (chain == null || chain.length == 0) throw new java.security.cert.CertificateException("Missing certificate");
                try {
                    for (X509Certificate certificate : chain) certificate.checkValidity();
                    byte[] digest = MessageDigest.getInstance("SHA-256").digest(chain[0].getPublicKey().getEncoded());
                    String actual = toHex(digest);
                    if (!MessageDigest.isEqual(actual.getBytes(StandardCharsets.US_ASCII), expectedPin.getBytes(StandardCharsets.US_ASCII))) {
                        throw new java.security.cert.CertificateException("Certificate pin mismatch");
                    }
                } catch (java.security.cert.CertificateException exception) { throw exception; }
                catch (Exception exception) { throw new java.security.cert.CertificateException("Certificate pin could not be checked", exception); }
            }
            @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
        };
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, new javax.net.ssl.TrustManager[] { trustManager }, new SecureRandom());
        return new OkHttpClient.Builder()
            .sslSocketFactory(context.getSocketFactory(), trustManager)
            .hostnameVerifier((candidate, session) -> host.equalsIgnoreCase(candidate))
            .followRedirects(false).followSslRedirects(false)
            .connectTimeout(10, TimeUnit.SECONDS).readTimeout(15, TimeUnit.SECONDS)
            .writeTimeout(10, TimeUnit.SECONDS).build();
    }

    private org.json.JSONObject readIdentity() throws Exception {
        String value = IdentityStore.read(getContext());
        if (value == null) throw new IOException("No saved pairing");
        return new org.json.JSONObject(value);
    }

    private static boolean validHost(String host) {
        if (host == null || host.isEmpty() || host.length() > 253 || host.contains(":") || host.contains("/") || host.contains("@")) return false;
        for (String label : host.split("\\.", -1)) {
            if (label.isEmpty() || label.length() > 63 || !label.matches("(?i)^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")) return false;
        }
        return true;
    }

    private static boolean validDeviceId(String id) { return id != null && id.length() <= 128 && id.matches("^[A-Za-z0-9_-]+$"); }
    private static boolean validHex(String value, int bytes) {
        return value != null && !value.isEmpty() && (bytes < 0 || value.length() == bytes * 2) && value.matches("(?i)^[0-9a-f]+$");
    }
    private static String toHex(byte[] bytes) {
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) result.append(String.format(Locale.ROOT, "%02x", value & 0xff));
        return result.toString();
    }

    private static final class IdentityStore {
        private static final String ALIAS = "wand-night-stand-local-device-v1";
        private static final String PREFS = "native-device-identity";

        static String read(Context context) throws Exception {
            String encoded = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString("identity", null);
            if (encoded == null) return null;
            byte[] envelope = Base64.decode(encoded, Base64.NO_WRAP);
            if (envelope.length < 13) throw new IOException("Invalid encrypted identity");
            byte[] nonce = Arrays.copyOfRange(envelope, 0, 12);
            byte[] cipherText = Arrays.copyOfRange(envelope, 12, envelope.length);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, nonce));
            return new String(cipher.doFinal(cipherText), StandardCharsets.UTF_8);
        }

        static void write(Context context, String value) throws Exception {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.ENCRYPT_MODE, key());
            byte[] nonce = cipher.getIV();
            if (nonce == null || nonce.length != 12) throw new IOException("Identity encryption failed");
            byte[] encrypted = cipher.doFinal(value.getBytes(StandardCharsets.UTF_8));
            byte[] envelope = new byte[nonce.length + encrypted.length];
            System.arraycopy(nonce, 0, envelope, 0, nonce.length);
            System.arraycopy(encrypted, 0, envelope, nonce.length, encrypted.length);
            if (!context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                .putString("identity", Base64.encodeToString(envelope, Base64.NO_WRAP)).commit()) {
                throw new IOException("Identity could not be stored");
            }
            Arrays.fill(encrypted, (byte) 0);
            Arrays.fill(envelope, (byte) 0);
        }

        static void clear(Context context) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().clear().commit();
        }

        private static SecretKey key() throws Exception {
            KeyStore store = KeyStore.getInstance("AndroidKeyStore");
            store.load(null);
            java.security.Key existing = store.getKey(ALIAS, null);
            if (existing instanceof SecretKey) return (SecretKey) existing;
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true).build());
            return generator.generateKey();
        }
    }
}
