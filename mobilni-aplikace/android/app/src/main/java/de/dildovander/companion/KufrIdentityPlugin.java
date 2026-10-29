package de.dildovander.companion;

import android.content.Context;
import android.content.Intent;
import android.app.Activity;
import android.net.Uri;
import android.os.Build;
import android.webkit.CookieManager;
import androidx.core.content.FileProvider;
import androidx.core.content.ContextCompat;
import androidx.credentials.CreatePublicKeyCredentialRequest;
import androidx.credentials.Credential;
import androidx.credentials.CredentialManager;
import androidx.credentials.CredentialManagerCallback;
import androidx.credentials.GetCredentialRequest;
import androidx.credentials.GetCredentialResponse;
import androidx.credentials.GetPublicKeyCredentialOption;
import androidx.credentials.PublicKeyCredential;
import androidx.credentials.CreatePublicKeyCredentialResponse;
import androidx.credentials.exceptions.CreateCredentialException;
import androidx.credentials.exceptions.GetCredentialException;
import androidx.credentials.exceptions.NoCredentialException;

import androidx.annotation.NonNull;
import androidx.activity.result.ActivityResult;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import com.getcapacitor.annotation.ActivityCallback;

import java.io.IOException;
import java.io.File;
import java.io.FileOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.HashSet;
import java.util.Arrays;
import java.util.concurrent.ConcurrentHashMap;

import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.Headers;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import okhttp3.ResponseBody;

@CapacitorPlugin(name = "KufrIdentity")
public final class KufrIdentityPlugin extends Plugin {
    private static final int MAX_BODY = 32 * 1024 * 1024;
    private static final int MAX_PUBLIC_RESOURCE = 96 * 1024 * 1024;
    private static final long MAX_AUTHENTICATED_DOWNLOAD = 2_147_483_648L;
    private static final Set<String> RESPONSE_HEADERS = new HashSet<>(Arrays.asList("content-type", "retry-after", "content-length"));
    private static final Set<String> METHODS = new HashSet<>(Arrays.asList("GET", "POST", "PUT", "PATCH", "DELETE"));
    private final OkHttpClient client = new OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .callTimeout(90, java.util.concurrent.TimeUnit.SECONDS).build();
    private final Map<String, Call> accountCalls = new ConcurrentHashMap<>();
    private final Map<String, AccountRequestCancellation> accountOperations = new ConcurrentHashMap<>();
    private final Map<String, File> chronicleShareDirectories = new ConcurrentHashMap<>();
    private final ExecutorService ceremonyExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService transportExecutor = Executors.newCachedThreadPool();
    private volatile boolean ceremonyActive;

    @Override public void load() {
        super.load();
        cleanupExpiredChronicleShares();
    }

    @PluginMethod
    public void guestCapabilities(PluginCall call) {
        JSObject result = new JSObject();
        result.put("available", true);
        result.put("origin", origin());
        call.resolve(result);
    }

    @PluginMethod
    public void pushAvailability(PluginCall call) {
        // The same web bundle also ships in APKs built without Firebase config.
        // Never call FirebaseMessaging.getInstance() in those builds.
        int resource = getContext().getResources().getIdentifier("google_app_id", "string", getContext().getPackageName());
        JSObject result = new JSObject();
        result.put("available", resource != 0 && !getContext().getString(resource).isEmpty());
        call.resolve(result);
    }

    @PluginMethod
    public void publicOrigin(PluginCall call) {
        JSObject result = new JSObject(); result.put("origin", origin()); call.resolve(result);
    }

    @PluginMethod
    public void accountTransport(PluginCall call) {
        AccountRequestCancellation operation = admitAccountRequest(call);
        if (operation == null && validRequestId(call.getString("requestId", ""))) return;
        transportExecutor.execute(() -> perform(call, true, false, operation));
    }

    @PluginMethod
    public void guestRequest(PluginCall call) { transportExecutor.execute(() -> perform(call, false, false, null)); }

    @PluginMethod
    public void request(PluginCall call) {
        AccountRequestCancellation operation = admitAccountRequest(call);
        if (operation == null && validRequestId(call.getString("requestId", ""))) return;
        if (Boolean.TRUE.equals(call.getBoolean("confirmed"))) {
            new android.app.AlertDialog.Builder(getActivity())
                .setTitle("Confirm account change")
                .setMessage(call.getString("confirmation", "Confirm this account change?"))
                .setNegativeButton("Cancel", (dialog, which) -> {
                    cancelAdmittedRequest(call, operation);
                    call.reject("Request was cancelled.", "cancelled");
                })
                .setPositiveButton("Continue", (dialog, which) -> transportExecutor.execute(() -> perform(call, true, true, operation)))
                .show();
        } else transportExecutor.execute(() -> perform(call, true, true, operation));
    }

    private AccountRequestCancellation admitAccountRequest(PluginCall call) {
        String requestId = call.getString("requestId", "");
        if (!validRequestId(requestId)) return null;
        AccountRequestCancellation operation = new AccountRequestCancellation();
        if (accountOperations.putIfAbsent(requestId, operation) != null) {
            call.reject("An account request with this identifier is already active.", "busy");
            return null;
        }
        return operation;
    }

    private void cancelAdmittedRequest(PluginCall call, AccountRequestCancellation operation) {
        if (operation == null) return;
        accountOperations.remove(call.getString("requestId", ""), operation);
        operation.cancel();
    }

    @PluginMethod
    public void cancelAccountRequest(PluginCall call) {
        String requestId = call.getString("requestId", "");
        AccountRequestCancellation operation = accountOperations.remove(requestId);
        if (operation != null) operation.cancel();
        Call active = accountCalls.remove(requestId);
        if (active != null) active.cancel();
        JSObject result = new JSObject(); result.put("cancelled", active != null || operation != null); call.resolve(result);
    }

    @PluginMethod
    public void passkey(PluginCall call) {
        String mode = call.getString("mode", "");
        if (!Arrays.asList("signin", "enroll", "customer-enroll", "add", "reauth").contains(mode)) {
            call.reject("Passkey operation is invalid.", "invalid_request"); return;
        }
        synchronized (this) {
            if (ceremonyActive) { call.reject("Another account verification is active.", "busy"); return; }
            ceremonyActive = true;
        }
        ceremonyExecutor.execute(() -> beginPasskeyCeremony(call, mode));
    }

    @PluginMethod
    public void shareGuestFile(PluginCall call) { shareFile(call, "filename", "mediaType", "bodyBase64", 32 * 1024 * 1024); }

    @PluginMethod
    public void shareAccountDataFile(PluginCall call) {
        String requestId = call.getString("requestId", "");
        if (!validRequestId(requestId)) { call.reject("Invalid export request.", "invalid_request"); return; }
        shareFile(call, "filename", "mediaType", "bodyBase64", 32 * 1024 * 1024);
    }

    @PluginMethod
    public void shareChronicleImage(PluginCall call) {
        String requestId = call.getString("requestId", "");
        String filename = call.getString("filename", "");
        String mediaType = call.getString("mediaType", "");
        String encoded = call.getString("bodyBase64", "");
        if (!validRequestId(requestId) || !filename.matches("(?i)^vzpominka-[0-9a-f-]{36}\\.(png|jpg|webp)$") ||
            !("image/png".equals(mediaType) && filename.toLowerCase(Locale.ROOT).endsWith(".png") ||
              "image/jpeg".equals(mediaType) && filename.toLowerCase(Locale.ROOT).endsWith(".jpg") ||
              "image/webp".equals(mediaType) && filename.toLowerCase(Locale.ROOT).endsWith(".webp")) ||
            encoded.length() > ((8 * 1024 * 1024 + 2) / 3) * 4) {
            call.reject("Invalid chronicle image.", "invalid_request"); return;
        }
        byte[] bytes;
        try { bytes = android.util.Base64.decode(encoded, android.util.Base64.DEFAULT); }
        catch (IllegalArgumentException exception) { call.reject("Invalid chronicle image.", "invalid_request"); return; }
        boolean valid = bytes.length > 0 && bytes.length <= 8 * 1024 * 1024;
        if (valid && "image/png".equals(mediaType)) valid = bytes.length >= 8 &&
            Arrays.equals(Arrays.copyOf(bytes, 8), new byte[] {(byte) 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a});
        else if (valid && "image/jpeg".equals(mediaType)) valid = bytes.length >= 3 &&
            bytes[0] == (byte) 0xff && bytes[1] == (byte) 0xd8 && bytes[2] == (byte) 0xff;
        else if (valid && "image/webp".equals(mediaType)) valid = bytes.length >= 12 &&
            new String(bytes, 0, 4, StandardCharsets.US_ASCII).equals("RIFF") &&
            new String(bytes, 8, 4, StandardCharsets.US_ASCII).equals("WEBP");
        if (!valid) { Arrays.fill(bytes, (byte) 0); call.reject("Invalid chronicle image.", "invalid_request"); return; }
        shareChronicleImageFile(call, requestId, filename, mediaType, bytes);
    }

    private void shareChronicleImageFile(PluginCall call, String requestId, String filename, String mediaType, byte[] bytes) {
        File directory = null;
        try {
            cleanupExpiredChronicleShares();
            File root = new File(getContext().getCacheDir(), "diagnostics");
            directory = new File(root, "WandChronicleShare-" + requestId);
            if (!directory.mkdirs()) throw new IOException("share_failed");
            File file = new File(directory, filename);
            try (FileOutputStream output = new FileOutputStream(file)) { output.write(bytes); }
            Uri uri = FileProvider.getUriForFile(getContext(), getContext().getPackageName() + ".fileprovider", file);
            Intent send = new Intent(Intent.ACTION_SEND).setType(mediaType).putExtra(Intent.EXTRA_STREAM, uri);
            send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            chronicleShareDirectories.put(requestId, directory);
            startActivityForResult(call, Intent.createChooser(send, "Sdílet obrázek z kroniky"), "chronicleShareCompleted");
            directory = null; // The receiving app retains URI access until the chooser returns.
        } catch (Exception exception) {
            if (directory != null) deleteTree(directory);
            chronicleShareDirectories.remove(requestId);
            call.reject("Obrázek z kroniky se nepodařilo sdílet.", "share_failed");
        } finally { Arrays.fill(bytes, (byte) 0); }
    }

    @ActivityCallback
    private void chronicleShareCompleted(PluginCall call, ActivityResult result) {
        if (call == null) return;
        String requestId = call.getString("requestId", "");
        File directory = chronicleShareDirectories.remove(requestId);
        if (directory != null) deleteTree(directory);
        JSObject value = new JSObject();
        value.put("completed", result != null && result.getResultCode() != Activity.RESULT_CANCELED);
        call.resolve(value);
    }

    private void cleanupExpiredChronicleShares() {
        File root = new File(getContext().getCacheDir(), "diagnostics");
        File[] entries = root.listFiles();
        if (entries == null) return;
        long cutoff = System.currentTimeMillis() - java.util.concurrent.TimeUnit.DAYS.toMillis(1);
        for (File entry : entries) {
            String name = entry.getName();
            if (!name.startsWith("WandChronicleShare-")) continue;
            String requestId = name.substring("WandChronicleShare-".length());
            if (!chronicleShareDirectories.containsKey(requestId) && entry.lastModified() < cutoff) deleteTree(entry);
        }
    }

    @PluginMethod
    public void shareGuestLink(PluginCall call) {
        String url = call.getString("url", "");
        try {
            java.net.URI parsed = java.net.URI.create(url);
            String expectedHost = java.net.URI.create(origin()).getHost();
            if (!"https".equals(parsed.getScheme()) || !expectedHost.equalsIgnoreCase(parsed.getHost()) || parsed.getPort() != -1 ||
                !"/keepsake/".equals(parsed.getPath()) || parsed.getRawQuery() != null || parsed.getFragment() == null || parsed.getFragment().length() > 1024) {
                call.reject("Invalid guest link.", "invalid_request"); return;
            }
            if (!parsed.getFragment().startsWith("s=") || !parsed.getFragment().matches("^s=[A-Za-z0-9_-]{32,256}$")) {
                call.reject("Invalid keepsake link.", "invalid_request"); return;
            }
            Intent send = new Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, url);
            send.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            getContext().startActivity(Intent.createChooser(send, "Share keepsake link").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            call.resolve();
        } catch (Exception exception) { call.reject("Guest link could not be shared.", "share_failed"); }
    }

    @PluginMethod
    public void publicResource(PluginCall call) {
        String path = call.getString("path", "");
        int maxBytes = call.getInt("maxBytes", 0);
        String requestId = call.getString("requestId", "");
        boolean authenticated = Boolean.TRUE.equals(call.getBoolean("authenticated"));
        if (!validRequestId(requestId) || !validPublicResourcePath(path, maxBytes)) {
            call.reject("Invalid resource request.", "invalid_request"); return;
        }
        Request.Builder builder = new Request.Builder().url(origin() + path).get();
        if (authenticated) {
            String cookie = CookieManager.getInstance().getCookie(origin());
            if (cookie != null && !cookie.isEmpty()) builder.header("Cookie", cookie);
        }
        Call active = client.newCall(builder.build());
        accountCalls.put(requestId, active);
        active.enqueue(new Callback() {
            @Override public void onFailure(@NonNull Call ignored, @NonNull IOException error) {
                accountCalls.remove(requestId, active); call.reject("Resource is unavailable.", "network_unavailable");
            }
            @Override public void onResponse(@NonNull Call ignored, @NonNull Response response) {
                try (response) {
                    byte[] bytes = response.body() == null ? new byte[0] : readLimited(response.body(), maxBytes);
                    if (bytes.length > maxBytes) { call.reject("Resource is too large.", "response_too_large"); return; }
                    if (active.isCanceled()) { call.reject("Resource request was cancelled.", "cancelled"); return; }
                    JSObject result = new JSObject(); result.put("status", response.code());
                    result.put("bodyBase64", android.util.Base64.encodeToString(bytes, android.util.Base64.NO_WRAP));
                    org.json.JSONObject headers = new org.json.JSONObject();
                    String type = response.header("Content-Type"); if (type != null) headers.put("content-type", type);
                    String length = response.header("Content-Length"); if (length != null) headers.put("content-length", length);
                    result.put("headers", headers); call.resolve(result); Arrays.fill(bytes, (byte) 0);
                } catch (Exception exception) { call.reject(active.isCanceled() ? "Resource request was cancelled." : "Resource response was invalid.", active.isCanceled() ? "cancelled" : "invalid_response"); }
                finally { accountCalls.remove(requestId, active); }
            }
        });
    }

    @PluginMethod
    public void cancelPublicResource(PluginCall call) { cancelAccountRequest(call); }

    @PluginMethod
    public void shareAuthenticatedResource(PluginCall call) {
        String requestId = call.getString("requestId", "");
        String path = call.getString("path", "");
        String filename = call.getString("filename", "");
        long maxBytes = call.getData().optLong("maxBytes", 0);
        Long expectedBytes = call.getData().has("expectedBytes") ? call.getData().optLong("expectedBytes") : null;
        if (!validRequestId(requestId) || !validAuthenticatedDownloadPath(path) ||
            !filename.matches("^[A-Za-z0-9][A-Za-z0-9_.-]{0,159}$") || filename.contains("..") ||
            maxBytes < 1 || maxBytes > MAX_AUTHENTICATED_DOWNLOAD || (expectedBytes != null && (expectedBytes < 0 || expectedBytes > maxBytes))) {
            call.reject("Invalid authenticated resource request.", "invalid_request"); return;
        }
        String cookie = CookieManager.getInstance().getCookie(origin());
        if (cookie == null || cookie.isEmpty()) { call.reject("Sign in before sharing this resource.", "account_required"); return; }
        Call networkCall = client.newCall(new Request.Builder().url(origin() + path).get().header("Origin", origin()).header("Cookie", cookie).header("Cache-Control", "no-store").build());
        accountCalls.put(requestId, networkCall);
        networkCall.enqueue(new Callback() {
            @Override public void onFailure(@NonNull Call ignored, @NonNull IOException error) {
                accountCalls.remove(requestId, networkCall);
                call.reject("Resource could not be downloaded.", networkCall.isCanceled() ? "cancelled" : "network_unavailable");
            }
            @Override public void onResponse(@NonNull Call ignored, @NonNull Response response) {
                File directory = new File(new File(getContext().getCacheDir(), "diagnostics"), "WandShare-" + UUID.randomUUID());
                File file = new File(directory, filename);
                try (response) {
                    if (!response.isSuccessful() || response.body() == null || !directory.mkdirs()) throw new IOException("download_failed");
                    String mediaType = response.header("Content-Type", "").split(";", 2)[0].trim().toLowerCase(Locale.ROOT);
                    if (!validAuthenticatedDownloadMime(mediaType, path)) throw new IOException("invalid_mime");
                    long declared = response.body().contentLength();
                    if (declared > maxBytes || (expectedBytes != null && declared >= 0 && declared != expectedBytes)) throw new IOException("invalid_size");
                    long written = 0;
                    try (java.io.InputStream input = response.body().byteStream(); FileOutputStream output = new FileOutputStream(file)) {
                        byte[] buffer = new byte[8192];
                        int count;
                        while ((count = input.read(buffer)) != -1) {
                            written += count;
                            if (written > maxBytes || (expectedBytes != null && written > expectedBytes)) throw new IOException("invalid_size");
                            output.write(buffer, 0, count);
                        }
                    }
                    if (expectedBytes != null && written != expectedBytes) throw new IOException("invalid_size");
                    if (networkCall.isCanceled() || !cookie.equals(CookieManager.getInstance().getCookie(origin()))) throw new IOException("cancelled");
                    Uri uri = FileProvider.getUriForFile(getContext(), getContext().getPackageName() + ".fileprovider", file);
                    Intent send = new Intent(Intent.ACTION_SEND).setType(mediaType).putExtra(Intent.EXTRA_STREAM, uri);
                    send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
                    getActivity().runOnUiThread(() -> {
                        try {
                            if (networkCall.isCanceled() || !cookie.equals(CookieManager.getInstance().getCookie(origin()))) throw new IOException("cancelled");
                            getContext().startActivity(Intent.createChooser(send, "Share model file").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
                            accountCalls.remove(requestId, networkCall);
                            JSObject result = new JSObject(); result.put("completed", true); call.resolve(result);
                        } catch (Exception exception) {
                            accountCalls.remove(requestId, networkCall); deleteTree(directory); call.reject("Resource could not be shared.", "share_failed");
                        }
                    });
                } catch (Exception exception) {
                    accountCalls.remove(requestId, networkCall);
                    deleteTree(directory);
                    call.reject("Authenticated resource could not be shared.", networkCall.isCanceled() ? "cancelled" : ("invalid_size".equals(exception.getMessage()) ? "response_too_large" : "share_failed"));
                }
            }
        });
    }
    private void shareFile(PluginCall call, String fileKey, String typeKey, String bodyKey, int maximumBytes) {
        String name = call.getString(fileKey, "");
        String mediaType = call.getString(typeKey, "application/octet-stream");
        String encoded = call.getString(bodyKey, "");
        if (!new HashSet<>(Arrays.asList("image/png", "image/jpeg", "image/webp", "image/svg+xml", "text/plain", "application/zip", "application/json")).contains(mediaType) ||
            name.length() > 160 || name.contains("..") || name.contains("/") || name.contains("\\") ||
            !name.matches("^[A-Za-z0-9][A-Za-z0-9_.-]*$") || encoded.length() > ((maximumBytes + 2) / 3) * 4) {
            call.reject("File cannot be shared.", "invalid_request"); return;
        }
        byte[] bytes;
        try { bytes = android.util.Base64.decode(encoded, android.util.Base64.DEFAULT); }
        catch (IllegalArgumentException exception) { call.reject("File cannot be shared.", "invalid_request"); return; }
        if (bytes.length > maximumBytes) { call.reject("File is too large.", "invalid_request"); return; }
        File directory = new File(new File(getContext().getCacheDir(), "diagnostics"), "WandShare-" + UUID.randomUUID());
        if (!directory.mkdirs()) { call.reject("File could not be prepared.", "share_failed"); return; }
        File file = new File(directory, name);
        try {
            try (FileOutputStream output = new FileOutputStream(file)) { output.write(bytes); }
            Arrays.fill(bytes, (byte) 0);
            Uri uri = FileProvider.getUriForFile(getContext(), getContext().getPackageName() + ".fileprovider", file);
            Intent send = new Intent(Intent.ACTION_SEND).setType(mediaType).putExtra(Intent.EXTRA_STREAM, uri);
            send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
            getContext().startActivity(Intent.createChooser(send, "Share file").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            if (call.getData().has("requestId")) { JSObject result = new JSObject(); result.put("completed", true); call.resolve(result); }
            else call.resolve();
        } catch (Exception exception) {
            deleteTree(directory); call.reject("File could not be shared.", "share_failed");
        }
    }

    private static void deleteTree(File file) {
        File[] children = file.listFiles(); if (children != null) for (File child : children) deleteTree(child); file.delete();
    }

    private static byte[] readLimited(ResponseBody body, int limit) throws IOException {
        if (body.contentLength() > limit) throw new IOException("response_too_large");
        try (InputStream input = body.byteStream(); ByteArrayOutputStream output = new ByteArrayOutputStream(Math.min(limit, 65536))) {
            byte[] buffer = new byte[8192];
            int count;
            long total = 0;
            while ((count = input.read(buffer)) != -1) {
                total += count;
                if (total > limit) throw new IOException("response_too_large");
                output.write(buffer, 0, count);
            }
            return output.toByteArray();
        }
    }
    private static boolean validPublicPath(String path) {
        return path.startsWith("/") && !path.startsWith("//") && path.length() < 4096 && !path.contains("\\") &&
            !path.contains("#") && !path.contains("..") && !path.contains("%") && !path.contains("@") && !path.contains("\r") && !path.contains("\n");
    }

    private static boolean validPublicResourcePath(String path, int maxBytes) {
        if (!validPublicPath(path) || maxBytes < 1) return false;
        if ("/models/active.json".equals(path)) return maxBytes <= 131072;
        if ("/downloads/index.json".equals(path)) return maxBytes <= 262144;
        String prefix = "/models/bundles/";
        if (!path.startsWith(prefix)) return false;
        String[] parts = path.substring(prefix.length()).split("/", -1);
        if (parts.length < 2 || !parts[0].matches("^[0-9a-f]{64}$")) return false;
        String asset = String.join("/", Arrays.copyOfRange(parts, 1, parts.length));
        if (asset.isEmpty() || asset.length() > 240 || !asset.matches("^[A-Za-z0-9_-]+(?:\\.[A-Za-z0-9_-]+)*(?:/[A-Za-z0-9_-]+(?:\\.[A-Za-z0-9_-]+)*)*$")) return false;
        return maxBytes <= ("model-bundle.json".equals(asset) ? 131072 : MAX_PUBLIC_RESOURCE);
    }

    private static boolean validAuthenticatedDownloadPath(String path) {
        if (!validPublicPath(path) || path.contains("%") || !path.startsWith("/downloads/releases/")) return false;
        String[] parts = path.substring("/downloads/releases/".length()).split("/", -1);
        return parts.length == 2 && parts[0].matches("^[0-9a-f]{64}$") &&
            parts[1].matches("^[A-Za-z0-9_-]+(?:\\.[A-Za-z0-9_-]+)*\\.(?:zip|pdf|step|stp|stl|3mf|dxf|txt)$");
    }

    static boolean validAuthenticatedDownloadMime(String mediaType, String path) {
        if (!Arrays.asList("application/octet-stream", "application/zip", "application/pdf", "text/plain", "model/step", "model/stl").contains(mediaType)) return false;
        String extension = path.substring(path.lastIndexOf('.') + 1).toLowerCase(Locale.ROOT);
        switch (extension) {
            case "zip": return "application/octet-stream".equals(mediaType) || "application/zip".equals(mediaType);
            case "pdf": return "application/octet-stream".equals(mediaType) || "application/pdf".equals(mediaType);
            case "step": case "stp": return "application/octet-stream".equals(mediaType) || "model/step".equals(mediaType);
            case "stl": return "application/octet-stream".equals(mediaType) || "model/stl".equals(mediaType);
            case "txt": return "application/octet-stream".equals(mediaType) || "text/plain".equals(mediaType);
            case "3mf": case "dxf": return "application/octet-stream".equals(mediaType);
            default: return false;
        }
    }

    private void perform(PluginCall call, boolean account, boolean jsonBody, AccountRequestCancellation operation) {
        String suppliedRequestId = call.getString("requestId", "");
        final String requestId = account || !suppliedRequestId.isEmpty() ? suppliedRequestId : UUID.randomUUID().toString();
        String path = call.getString("path", "");
        String method = call.getString("method", "GET").toUpperCase(Locale.ROOT);
        if ((account && !validRequestId(requestId)) || !(account ? NativeAccountRequestPolicy.allows(path, method) : validPath(path, false)) || !METHODS.contains(method)) {
            releaseOperation(requestId, operation);
            call.reject("Invalid account request.", "invalid_request"); return;
        }
        String encodedBody = account ? call.getString("bodyBase64") : call.getString("bodyBase64");
        String textBody = jsonBody ? call.getString("body") : null;
        byte[] body;
        try {
            body = jsonBody ? (textBody == null ? new byte[0] : textBody.getBytes(StandardCharsets.UTF_8))
                : (encodedBody == null ? new byte[0] : android.util.Base64.decode(encodedBody, android.util.Base64.DEFAULT));
        } catch (IllegalArgumentException exception) { releaseOperation(requestId, operation); call.reject("Invalid request body.", "invalid_request"); return; }
        String base = origin();
        String cookie = CookieManager.getInstance().getCookie(base);
        Request.Builder builder = new Request.Builder().url(base + path).method(method, requestBody(method, body, call));
        org.json.JSONObject suppliedHeaders = call.getObject("headers");
        java.util.Map<String, String> requestHeaders = new java.util.HashMap<>();
        if (suppliedHeaders != null) {
            java.util.Iterator<String> names = suppliedHeaders.keys();
            while (names.hasNext()) {
                String name = names.next();
                Object raw = suppliedHeaders.opt(name);
                if (!(raw instanceof String)) { releaseOperation(requestId, operation); call.reject("Invalid request headers.", "invalid_request"); return; }
                requestHeaders.put(name, (String) raw);
            }
        }
        String contentType = requestHeaders.entrySet().stream()
            .filter(entry -> "content-type".equalsIgnoreCase(entry.getKey()))
            .map(Map.Entry::getValue).findFirst().orElse(null);
        int requestLimit = account
            ? NativeAccountRequestPolicy.requestBodyLimit(path, method, contentType)
            : MAX_BODY;
        if (body.length > requestLimit) {
            releaseOperation(requestId, operation);
            call.reject("Request is too large.", "invalid_request"); return;
        }
        if (account) {
            try {
                String csrf = "GET".equals(method) ? null : fetchCsrf(operation);
                operation.requireActive();
                NativeAccountRequestPolicy.apply(builder, base, cookie, csrf, method,
                    Boolean.TRUE.equals(call.getBoolean("confirmed")), requestHeaders, path);
            } catch (IllegalArgumentException exception) {
                accountOperations.remove(requestId, operation);
                call.reject("Invalid account request.", "invalid_request"); return;
            } catch (IOException exception) {
                accountOperations.remove(requestId, operation);
                boolean cancelled = operation.isCancelled();
                call.reject(cancelled ? "Request was cancelled." : "Account session could not be verified.", cancelled ? "cancelled" : "csrf_required"); return;
            }
        } else {
            builder.header("Origin", base);
            if (cookie != null && !cookie.isEmpty()) builder.header("Cookie", cookie);
            for (Map.Entry<String, String> entry : requestHeaders.entrySet()) {
                String lower = entry.getKey().toLowerCase(Locale.ROOT);
                String value = entry.getValue();
                if (!NativeGuestHeaderPolicy.allows(path, method, lower, value)) {
                    call.reject("Invalid request headers.", "invalid_request"); return;
                }
                builder.header(entry.getKey(), value);
            }
        }
        try { if (operation != null) operation.requireActive(); }
        catch (IOException exception) { accountOperations.remove(requestId, operation); call.reject("Request was cancelled.", "cancelled"); return; }
        Call networkCall = client.newCall(builder.build());
        AccountRequestCancellation.Cancellable cancellable = networkCall::cancel;
        if (operation != null && !operation.attach(cancellable)) {
            accountOperations.remove(requestId, operation); call.reject("Request was cancelled.", "cancelled"); return;
        }
        if (account) accountCalls.put(requestId, networkCall);
        networkCall.enqueue(new Callback() {
            @Override public void onFailure(@NonNull Call ignored, @NonNull IOException exception) {
                accountCalls.remove(requestId, networkCall);
                if (operation != null) { operation.finish(cancellable); accountOperations.remove(requestId, operation); }
                call.reject("Account service is unavailable.", networkCall.isCanceled() ? "cancelled" : "network_unavailable");
            }
            @Override public void onResponse(@NonNull Call ignored, @NonNull Response response) {
                try (response) {
                    CookieManager cookies = CookieManager.getInstance();
                    for (String setCookie : response.headers("Set-Cookie")) cookies.setCookie(base, setCookie);
                    cookies.flush();
                    int responseLimit = account
                        ? NativeAccountRequestPolicy.responseBodyLimit(path, method)
                        : MAX_BODY;
                    byte[] bytes = response.body() == null ? new byte[0] : readLimited(response.body(), responseLimit);
                    if (bytes.length > responseLimit) { call.reject("Server response is too large.", "response_too_large"); return; }
                    if (networkCall.isCanceled()) { call.reject("Request was cancelled.", "cancelled"); return; }
                    JSObject result = new JSObject();
                    result.put("status", response.code());
                    result.put("bodyBase64", android.util.Base64.encodeToString(bytes, android.util.Base64.NO_WRAP));
                    org.json.JSONObject headers = new org.json.JSONObject();
                    for (String name : RESPONSE_HEADERS) {
                        String value = response.header(name);
                        if (value != null) headers.put(name, value);
                    }
                    result.put("headers", headers);
                    if (jsonBody) result.put("body", new String(bytes, StandardCharsets.UTF_8));
                    call.resolve(result);
                    Arrays.fill(bytes, (byte) 0);
                } catch (Exception exception) { call.reject(networkCall.isCanceled() ? "Request was cancelled." : "Server response was invalid.", networkCall.isCanceled() ? "cancelled" : "invalid_response"); }
                finally {
                    accountCalls.remove(requestId, networkCall);
                    if (operation != null) { operation.finish(cancellable); accountOperations.remove(requestId, operation); }
                }
            }
        });
    }

    private void releaseOperation(String requestId, AccountRequestCancellation operation) {
        if (operation != null) accountOperations.remove(requestId, operation);
    }

    private void beginPasskeyCeremony(PluginCall call, String mode) {
        String optionsPath;
        String completionPath;
        org.json.JSONObject body = new org.json.JSONObject();
        try {
            switch (mode) {
                case "signin": optionsPath = "/app/v1/passkeys/request-options"; completionPath = "/app/v1/passkeys/sign-in"; break;
                case "reauth": optionsPath = "/app/v1/passkeys/reauthentication-options"; completionPath = "/app/v1/passkeys/reauthenticate"; break;
                case "add": optionsPath = "/app/v1/passkeys/options"; completionPath = "/app/v1/passkeys/complete"; break;
                case "customer-enroll": optionsPath = "/app/v1/customer/enrollment/options"; completionPath = "/app/v1/customer/enrollment/complete";
                    body.put("displayName", call.getString("displayName", "")); body.put("openAiConsent", call.getBoolean("openAiConsent", false)); break;
                case "enroll": optionsPath = "/app/v1/enrollment/options"; completionPath = "/app/v1/enrollment/complete";
                    body.put("token", call.getString("token", "")); body.put("displayName", call.getString("displayName", "")); body.put("openAiConsent", call.getBoolean("openAiConsent", false)); break;
                default: throw new IOException("Unsupported credential flow");
            }
            String csrf = fetchCsrf();
            org.json.JSONObject options = postJson(optionsPath, csrf, body);
            validateRelyingParty(mode, options);
            getActivity().runOnUiThread(() -> launchCredentialRequest(call, mode, completionPath, csrf, options));
        } catch (Exception exception) {
            finishCeremony(call, exception.getMessage() != null && exception.getMessage().contains("rp") ? "passkey_origin_mismatch" : "passkey_unavailable");
        }
    }

    private void launchCredentialRequest(PluginCall call, String mode, String completionPath, String csrf, org.json.JSONObject options) {
        if (Build.VERSION.SDK_INT < 28) { finishCeremony(call, "passkey_unavailable"); return; }
        if (getActivity() == null || getActivity().isFinishing() || getActivity().isDestroyed()) {
            finishCeremony(call, "cancelled"); return;
        }
        CredentialManager manager = CredentialManager.create(getContext());
        java.util.concurrent.Executor main = ContextCompat.getMainExecutor(getContext());
        if ("signin".equals(mode) || "reauth".equals(mode)) {
            GetPublicKeyCredentialOption option = new GetPublicKeyCredentialOption(options.toString());
            GetCredentialRequest request = new GetCredentialRequest.Builder().addCredentialOption(option).build();
            manager.getCredentialAsync(getActivity(), request, null, main, new CredentialManagerCallback<GetCredentialResponse, GetCredentialException>() {
                @Override public void onResult(GetCredentialResponse response) {
                    Credential credential = response.getCredential();
                    if (!(credential instanceof PublicKeyCredential)) { finishCeremony(call, "invalid_credential"); return; }
                    completePasskey(call, mode, completionPath, csrf, ((PublicKeyCredential) credential).getAuthenticationResponseJson());
                }
                @Override public void onError(@NonNull GetCredentialException error) {
                    finishCeremony(call, error instanceof NoCredentialException ? "passkey_not_found" : "passkey_cancelled");
                }
            });
        } else {
            CreatePublicKeyCredentialRequest request = new CreatePublicKeyCredentialRequest(options.toString());
            manager.createCredentialAsync(getActivity(), request, null, main, new CredentialManagerCallback<androidx.credentials.CreateCredentialResponse, CreateCredentialException>() {
                @Override public void onResult(androidx.credentials.CreateCredentialResponse response) {
                    if (!(response instanceof CreatePublicKeyCredentialResponse)) { finishCeremony(call, "invalid_credential"); return; }
                    completePasskey(call, mode, completionPath, csrf, ((CreatePublicKeyCredentialResponse) response).getRegistrationResponseJson());
                }
                @Override public void onError(@NonNull CreateCredentialException error) {
                    finishCeremony(call, "passkey_cancelled");
                }
            });
        }
    }

    private void completePasskey(PluginCall call, String mode, String completionPath, String csrf, String response) {
        ceremonyExecutor.execute(() -> {
            boolean completionStarted = false;
            try {
                org.json.JSONObject request = new org.json.JSONObject().put("credential", new org.json.JSONObject(response));
                String url = origin() + completionPath;
                Request.Builder builder = new Request.Builder().url(url).post(RequestBody.create(request.toString(), okhttp3.MediaType.parse("application/json; charset=utf-8")))
                    .header("Origin", origin()).header("X-DV-CSRF", csrf);
                addCookies(builder, origin());
                completionStarted = true;
                try (Response result = client.newCall(builder.build()).execute()) {
                    persistCookies(origin(), result);
                    if (result.code() != 204) {
                        String code = "passkey_rejected";
                        if ("customer-enroll".equals(mode)) {
                            if (result.code() >= 400) {
                                try {
                                    String body = result.body() == null ? "" : result.body().string();
                                    String serverCode = new org.json.JSONObject(body).optString("code", "");
                                    if (!serverCode.isEmpty()) code = serverCode;
                                } catch (Exception ignored) { /* Keep a definitive generic rejection. */ }
                            }
                            if (result.code() >= 500 && !"beta_enrollment_closed".equals(code)
                                && !"beta_enrollment_unavailable".equals(code)
                                && !"beta_enrollment_closed_or_changed".equals(code)) {
                                code = "customer_enrollment_completion_uncertain";
                            }
                        }
                        finishCeremony(call, code); return;
                    }
                    getActivity().runOnUiThread(() -> finishCeremony(call, null));
                }
            } catch (Exception exception) {
                finishCeremony(call, completionStarted && "customer-enroll".equals(mode)
                    ? "customer_enrollment_completion_uncertain" : "passkey_unavailable");
            }
        });
    }

    private String fetchCsrf() throws IOException { return fetchCsrf(null); }

    private String fetchCsrf(AccountRequestCancellation operation) throws IOException {
        Request.Builder builder = new Request.Builder().url(origin() + "/app/v1/session").get().header("Origin", origin());
        addCookies(builder, origin());
        Call call = client.newCall(builder.build());
        AccountRequestCancellation.Cancellable cancellable = call::cancel;
        if (operation != null && !operation.attach(cancellable)) throw new IOException("Request was cancelled");
        try (Response response = call.execute()) {
            persistCookies(origin(), response);
            if (response.code() != 200 || response.body() == null) throw new IOException("Unable to read account session");
            org.json.JSONObject session = new org.json.JSONObject(response.body().string());
            String csrf = session.optString("csrfToken", "");
            if (csrf.isEmpty() || csrf.length() > 256) throw new IOException("Invalid account session");
            return csrf;
        } catch (org.json.JSONException exception) { throw new IOException("Invalid account session", exception); }
        finally { if (operation != null) operation.finish(cancellable); }
    }

    private org.json.JSONObject postJson(String path, String csrf, org.json.JSONObject body) throws IOException {
        Request.Builder builder = new Request.Builder().url(origin() + path)
            .post(RequestBody.create(body.toString(), okhttp3.MediaType.parse("application/json; charset=utf-8")))
            .header("Origin", origin()).header("X-DV-CSRF", csrf);
        addCookies(builder, origin());
        try (Response response = client.newCall(builder.build()).execute()) {
            persistCookies(origin(), response);
            if (response.code() != 200 || response.body() == null || response.body().contentLength() > 131072) throw new IOException("Passkey options were rejected");
            String text = response.body().string();
            if (text.length() > 131072) throw new IOException("Passkey options are too large");
            return new org.json.JSONObject(text);
        } catch (org.json.JSONException exception) { throw new IOException("Passkey options are invalid", exception); }
    }

    private static void validateRelyingParty(String mode, org.json.JSONObject options) throws Exception {
        String expected = "dildovander.de";
        String rp = ("signin".equals(mode) || "reauth".equals(mode)) ? options.getString("rpId") : options.getJSONObject("rp").getString("id");
        if (!expected.equalsIgnoreCase(rp)) throw new IOException("rp mismatch");
        if (!("signin".equals(mode) || "reauth".equals(mode))) {
            org.json.JSONObject user = options.getJSONObject("user");
            if (user.getString("name").length() > 256 || user.getString("id").length() > 512) throw new IOException("Invalid passkey user");
        }
    }

    private void finishCeremony(PluginCall call, String code) {
        synchronized (this) { ceremonyActive = false; }
        if (code == null) call.resolve(); else call.reject("Passkey verification failed. Try again or use another sign-in method.", code);
    }

    private static void addCookies(Request.Builder builder, String origin) {
        String cookies = CookieManager.getInstance().getCookie(origin);
        if (cookies != null && !cookies.isEmpty()) builder.header("Cookie", cookies);
    }

    private static void persistCookies(String origin, Response response) {
        CookieManager manager = CookieManager.getInstance();
        for (String cookie : response.headers("Set-Cookie")) manager.setCookie(origin, cookie);
        manager.flush();
    }

    private RequestBody requestBody(String method, byte[] body, PluginCall call) {
        if (method.equals("GET") || method.equals("HEAD")) return null;
        String contentType = "application/json; charset=utf-8";
        org.json.JSONObject headers = call.getObject("headers");
        if (headers != null) contentType = headers.optString("content-type", contentType);
        return RequestBody.create(body, okhttp3.MediaType.parse(contentType));
    }

    private String origin() { return BuildConfig.ACCOUNT_ORIGIN; }
    private static boolean validRequestId(String value) { return value.length() <= 64 && value.matches("^[A-Za-z0-9_-]+$"); }
    private static boolean validPath(String path, boolean account) {
        if (path.length() > 4096 || !path.startsWith("/") || path.startsWith("//") || path.contains("\\") || path.contains("#") || path.contains("\r") || path.contains("\n")) return false;
        try {
            java.net.URI uri = java.net.URI.create(path);
            String route = uri.getRawPath();
            boolean permitted = account
                ? route.startsWith("/app/v1/") || route.startsWith("/v1/twin/instances") || route.startsWith("/v1/show/devices/") || route.equals("/v1/admin/show/chronicle")
                : route.startsWith("/guest/v1/");
            return !uri.isAbsolute() && permitted && route.equals(uri.getPath()) && route.split("/").length < 20;
        } catch (Exception exception) { return false; }
    }
}
