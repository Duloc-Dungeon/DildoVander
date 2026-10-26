package de.dildovander.companion;

import android.Manifest;
import android.content.Context;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.media.AudioManager;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.content.pm.PackageManager;
import android.webkit.CookieManager;

import androidx.annotation.NonNull;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import com.getcapacitor.annotation.Permission;
import com.getcapacitor.annotation.PermissionCallback;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

import okhttp3.Call;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import okhttp3.ResponseBody;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import okio.BufferedSource;

@CapacitorPlugin(name = "KufrDebug", permissions = {
    @Permission(alias = "microphone", strings = { Manifest.permission.RECORD_AUDIO })
})
public final class KufrDebugPlugin extends Plugin {
    private static final MediaType JSON = MediaType.get("application/json; charset=utf-8");
    private static final String CATALOG = "ollivander-private-party-v1";
    private static final int PCM_RATE = 24000;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService network = Executors.newCachedThreadPool();
    private final OkHttpClient client = new OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .connectTimeout(10, java.util.concurrent.TimeUnit.SECONDS).readTimeout(65, java.util.concurrent.TimeUnit.SECONDS)
        .writeTimeout(10, java.util.concurrent.TimeUnit.SECONDS).build();
    private String deviceId;
    private JSONObject engines;
    private LiveConversation active;
    private boolean pendingSetup;
    private PluginCall pendingProfileCall;
    private final ProfileGeneration profileGeneration = new ProfileGeneration();
    private final CompletedConversationSnapshot completed = new CompletedConversationSnapshot();

    @Override protected void handleOnPause() {
        super.handleOnPause();
        cancelPendingProfile();
        LiveConversation conversation = active;
        if (conversation != null) conversation.stop("cancelled");
    }

    @Override protected void handleOnStop() {
        super.handleOnStop();
        cancelPendingProfile();
        LiveConversation conversation = active;
        if (conversation != null) conversation.stop("cancelled");
    }

    @Override protected void handleOnDestroy() {
        cancelPendingProfile();
        LiveConversation conversation = active;
        if (conversation != null) conversation.stop("cancelled");
        network.shutdownNow();
        super.handleOnDestroy();
    }

    @PluginMethod
    public void status(PluginCall call) {
        JSObject result = new JSObject();
        boolean configured = deviceId != null;
        result.put("configured", configured);
        result.put("running", active != null || pendingSetup);
        result.put("session", active != null);
        result.put("recording", active != null && active.recording.get());
        result.put("handsFree", active != null);
        result.put("phase", active == null ? "idle" : active.phase);
        result.put("mode", configured ? "openai" : "unknown");
        result.put("accessMode", configured ? "account" : "none");
        result.put("deviceId", deviceId);
        result.put("engines", androidEngines(engines));
        result.put("inputActive", active != null && active.recording.get());
        result.put("outputActive", active != null && active.outputActive);
        result.put("conversationId", active == null ? JSONObject.NULL : active.conversationId);
        result.put("lastConversationId", active == null && completed.conversationId() != null ? completed.conversationId() : JSONObject.NULL);
        result.put("errorCode", active == null ? completed.errorCode() == null ? JSONObject.NULL : completed.errorCode() : active.errorCode == null ? JSONObject.NULL : active.errorCode);
        result.put("transcript", transcriptJson(active == null ? completed.transcript() : active.transcriptSnapshot()));
        Integer selected = active == null ? completed.selected() : active.selected >= 1 && active.selected <= 6 ? active.selected : null;
        result.put("selected", selected == null ? JSONObject.NULL : selected);
        result.put("nativeVersion", BuildConfig.VERSION_NAME);
        call.resolve(result);
    }

    private static JSONArray transcriptJson(List<CompletedConversationSnapshot.TranscriptEntry> entries) {
        JSONArray result = new JSONArray();
        for (CompletedConversationSnapshot.TranscriptEntry entry : entries) result.put(json("role", entry.role, "text", entry.text));
        return result;
    }

    private static Object androidEngines(JSONObject source) {
        if (source == null) return JSONObject.NULL;
        try {
            JSONObject filtered = new JSONObject(source.toString());
            JSONArray supportedEngines = filtered.optJSONArray("engines");
            if (supportedEngines != null) {
                for (int index = 0; index < supportedEngines.length(); index++) {
                    JSONObject engine = supportedEngines.optJSONObject(index);
                    if (engine != null) engine.put("cameraSources", new JSONArray());
                }
            }
            filtered.remove("cameras");
            return filtered;
        } catch (org.json.JSONException exception) { return JSONObject.NULL; }
    }

    @PluginMethod
    public void useAccount(PluginCall call) {
        String selected = call.getString("deviceId", "");
        if (!validDeviceId(selected) || active != null || pendingSetup) {
            call.reject("Choose an available device while no conversation is active.", "account_unavailable"); return;
        }
        String cookies = CookieManager.getInstance().getCookie(BuildConfig.ACCOUNT_ORIGIN);
        if (cookies == null || !cookies.contains("__Host-dv-account=")) {
            call.reject("Sign in again before starting a conversation.", "account_unavailable"); return;
        }
        pendingSetup = true;
        long requestedGeneration = profileGeneration.begin();
        pendingProfileCall = call;
        network.execute(() -> {
            try {
                String csrf = fetchCsrf();
                JSONObject diagnostics = accountRequest("GET", "/v1/diagnostics", null, selected, csrf, null, 16384);
                JSONObject inventory = diagnostics.optJSONObject("engines");
                if (inventory == null || !CATALOG.equals(diagnostics.optString("catalogVersion")) || !isLiveReady(inventory)) {
                    throw new IOException("gpt_live_unavailable");
                }
                main.post(() -> {
                    if (!profileGeneration.isCurrent(requestedGeneration) || pendingProfileCall != call || !hasAccountCookie()) {
                        call.reject("Account profile selection was cancelled.", "cancelled"); return;
                    }
                    pendingProfileCall = null;
                    pendingSetup = false; completed.clear(); deviceId = selected; engines = inventory; emitStatus(); call.resolve();
                });
            } catch (Exception exception) {
                main.post(() -> {
                    if (profileGeneration.isCurrent(requestedGeneration) && pendingProfileCall == call) {
                        pendingProfileCall = null; pendingSetup = false;
                    }
                    call.reject("Account access or GPT Live availability could not be verified.", safeCode(exception));
                });
            }
        });
    }

    private boolean hasAccountCookie() {
        String cookies = CookieManager.getInstance().getCookie(BuildConfig.ACCOUNT_ORIGIN);
        return cookies != null && cookies.contains("__Host-dv-account=");
    }

    private void cancelPendingProfile() {
        if (pendingProfileCall == null) return;
        profileGeneration.invalidate();
        PluginCall call = pendingProfileCall;
        pendingProfileCall = null;
        pendingSetup = false;
        call.reject("Account profile selection was cancelled.", "cancelled");
    }

    @PluginMethod
    public void startConversation(PluginCall call) {
        if (deviceId == null || active != null || pendingSetup) { call.reject("Sign in and select an authorized device first.", "unavailable"); return; }
        if (!"gpt-live".equals(call.getString("engine", "")) || !"openai".equals(call.getString("expectedMode", ""))) {
            call.reject("Android supports the GPT Live conversation mode only.", "native_upgrade_required"); return;
        }
        int readyMask = call.getInt("readyMask", -1);
        String voice = call.getString("voice", "cedar");
        if (readyMask < 0 || readyMask > 63 || !("cedar".equals(voice) || "marin".equals(voice) || "coral".equals(voice))) {
            call.reject("Conversation settings are invalid.", "invalid_request"); return;
        }
        JSONObject guestMemory = call.getObject("guestMemory");
        if (guestMemory != null && (!Boolean.TRUE.equals(call.getBoolean("confirmGuestMemory")) || !validGuestMemory(guestMemory))) {
            call.reject("Confirm the verified guest identity and memory consent before starting.", "guest_identity_not_verified"); return;
        }
        if (getContext().checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissionForAlias("microphone", call, "microphonePermissionResult"); return;
        }
        beginConversation(call, readyMask, voice, guestMemory);
    }

    @PermissionCallback
    private void microphonePermissionResult(PluginCall call) {
        if (getPermissionState("microphone") != com.getcapacitor.PermissionState.GRANTED) {
            call.reject("Microphone permission was denied.", "microphone_unavailable"); return;
        }
        beginConversation(call, call.getInt("readyMask", -1), call.getString("voice", "cedar"), call.getObject("guestMemory"));
    }

    private void beginConversation(PluginCall call, int readyMask, String voice, JSONObject guestMemory) {
        if (pendingSetup || active != null) { call.reject("Another conversation is active.", "busy"); return; }
        completed.clear();
        pendingSetup = true;
        String selectedDevice = deviceId;
        LiveConversation conversation = new LiveConversation(call, selectedDevice, readyMask, voice, guestMemory);
        active = conversation;
        conversation.start();
        emitStatus();
    }

    @PluginMethod
    public void stop(PluginCall call) {
        LiveConversation conversation = active;
        if (conversation != null) conversation.stop("cancelled");
        else { cancelPendingProfile(); completed.clear(); emitStatus(); }
        call.resolve();
    }

    @PluginMethod
    public void forgetProfile(PluginCall call) {
        if (active != null) { call.reject("Stop the active conversation before removing account access.", "busy"); return; }
        cancelPendingProfile();
        profileGeneration.invalidate();
        completed.clear();
        deviceId = null; engines = null; emitStatus(); call.resolve();
    }

    @PluginMethod
    public void configure(PluginCall call) {
        call.reject("Legacy service credentials are not supported on Android. Use your Wand Night Stand account.", "account_required");
    }

    @PluginMethod public void shareLog(PluginCall call) { call.reject("Use the diagnostic export control on Android.", "export_unavailable"); }
    @PluginMethod public void speakSystem(PluginCall call) { call.reject("System speech is unavailable during GPT Live on Android.", "unsupported"); }
    @PluginMethod public void readServerLogs(PluginCall call) {
        String level = call.getString("level", "info");
        Long after = call.getData().has("after") ? call.getData().optLong("after") : null;
        if (deviceId == null || !Arrays.asList("info", "warning", "error").contains(level) || (after != null && after < 0)) {
            call.reject("Choose an account device and a valid log filter.", "invalid_request"); return;
        }
        String path = "/v1/admin/logs?limit=200&level=" + level + (after == null ? "" : "&after=" + after);
        network.execute(() -> {
            try {
                JSONObject page = accountRequest("GET", path, null, deviceId, fetchCsrf(), null, 262144);
                if (!validServerLogPage(page)) throw new IOException("invalid_response");
                call.resolve(JSObject.fromJSONObject(page));
            } catch (Exception exception) { call.reject("Server logs could not be read.", safeCode(exception)); }
        });
    }
    @PluginMethod public void inspectBackend(PluginCall call) { call.reject("Backend inspection is unavailable on Android.", "unsupported"); }
    @PluginMethod public void inspectServer(PluginCall call) { call.reject("Server inspection is unavailable on Android.", "unsupported"); }
    @PluginMethod public void changeServerModes(PluginCall call) { call.reject("Server controls are unavailable on Android.", "unsupported"); }
    @PluginMethod public void inspectEvenings(PluginCall call) { call.reject("Evening inspection is unavailable on Android.", "unsupported"); }
    @PluginMethod public void createEvening(PluginCall call) { call.reject("Evening management is unavailable on Android.", "unsupported"); }
    @PluginMethod public void activateEvening(PluginCall call) { call.reject("Evening management is unavailable on Android.", "unsupported"); }
    @PluginMethod public void captureCostumePhoto(PluginCall call) { call.reject("Costume photo capture is unavailable on Android.", "unsupported"); }
    @PluginMethod public void discardCostumePhoto(PluginCall call) { call.resolve(); }
    @PluginMethod public void attachCostumePhoto(PluginCall call) { call.reject("Costume photo upload is unavailable on Android.", "unsupported"); }
    @PluginMethod public void inspectMemory(PluginCall call) { call.reject("Conversation memory inspection is unavailable on Android.", "unsupported"); }
    @PluginMethod public void deleteConversationMemory(PluginCall call) { call.reject("Conversation memory management is unavailable on Android.", "unsupported"); }

    private String fetchCsrf() throws IOException { return fetchCsrf(null); }

    private String fetchCsrf(LiveConversation owner) throws IOException {
        Request.Builder request = new Request.Builder().url(BuildConfig.ACCOUNT_ORIGIN + "/app/v1/session").get();
        addCookie(request);
        try (Response response = execute(owner, request.build())) {
            persistCookies(response);
            if (response.code() != 200 || response.body() == null || response.body().contentLength() > 131072) throw new IOException("account_required");
            JSONObject session = new JSONObject(readUtf8(response.body(), 131072));
            String csrf = session.optString("csrfToken", "");
            if (!session.has("account") || csrf.isEmpty() || csrf.length() > 256) throw new IOException("account_required");
            return csrf;
        } catch (org.json.JSONException exception) { throw new IOException("account_required", exception); }
    }

    private JSONObject accountRequest(String method, String path, JSONObject body, String targetDevice, String csrf, String idempotencyKey, int maxBytes) throws IOException {
        return accountRequest(method, path, body, targetDevice, csrf, idempotencyKey, maxBytes, false);
    }

    private JSONObject accountRequest(String method, String path, JSONObject body, String targetDevice, String csrf, String idempotencyKey, int maxBytes, boolean confirmed) throws IOException {
        return accountRequest(method, path, body, targetDevice, csrf, idempotencyKey, maxBytes, confirmed, null);
    }

    private JSONObject accountRequest(String method, String path, JSONObject body, String targetDevice, String csrf, String idempotencyKey, int maxBytes, boolean confirmed, LiveConversation owner) throws IOException {
        Request.Builder request = new Request.Builder().url(BuildConfig.ACCOUNT_ORIGIN + path).method(method,
            body == null ? null : RequestBody.create(body.toString(), JSON));
        addCookie(request);
        request.header("Origin", BuildConfig.ACCOUNT_ORIGIN);
        request.header("X-DV-Device-Id", targetDevice);
        request.header("X-DV-CSRF", csrf);
        if (confirmed) request.header("X-DV-Confirm", "yes");
        request.header("Accept", "application/json");
        request.header("Cache-Control", "no-store");
        if (body != null) request.header("Content-Type", "application/json");
        if (idempotencyKey != null) request.header("Idempotency-Key", idempotencyKey);
        try (Response response = execute(owner, request.build())) {
            persistCookies(response);
            if (response.code() < 200 || response.code() >= 300) throw new IOException(problemCode(response, "http_failed"));
            if (response.code() == 204) return new JSONObject();
            if (response.body() == null || response.body().contentLength() > maxBytes) throw new IOException("response_too_large");
            String text = readUtf8(response.body(), maxBytes);
            if (text.getBytes(StandardCharsets.UTF_8).length > maxBytes) throw new IOException("response_too_large");
            return new JSONObject(text);
        } catch (org.json.JSONException exception) { throw new IOException("invalid_response", exception); }
    }

    private Response execute(LiveConversation owner, Request request) throws IOException {
        if (owner == null) return client.newCall(request).execute();
        Call call = client.newCall(request);
        LiveSetupCancellation.Cancellable operation = call::cancel;
        if (!owner.cancellation.begin(operation)) throw new IOException("cancelled");
        try { return owner.cancellation.retainUntilClosed(call.execute(), operation); }
        catch (IOException | RuntimeException exception) {
            owner.cancellation.finish(operation);
            throw exception;
        }
    }

    private static String readUtf8(ResponseBody body, int limit) throws IOException {
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
            return new String(output.toByteArray(), StandardCharsets.UTF_8);
        }
    }
    private static JSONObject json(Object... values) {
        JSONObject object = new JSONObject();
        try {
            for (int index = 0; index < values.length; index += 2) object.put((String) values[index], values[index + 1]);
            return object;
        } catch (org.json.JSONException exception) { throw new IllegalStateException(exception); }
    }

    private String createConversation(String selectedDevice, String csrf, int readyMask, String bootId, LiveConversation owner) throws IOException {
        JSONObject consent = json("audio", true, "image", false);
        JSONObject body = json("deviceId", selectedDevice, "bootId", bootId, "generation", 0, "catalogVersion", CATALOG, "locale", "cs-CZ", "consent", consent);
        String key = UUID.randomUUID().toString().toLowerCase(java.util.Locale.ROOT);
        IOException failure = null;
        for (int attempt = 0; attempt < 2; attempt++) {
            try {
                owner.requireActive();
                JSONObject result = accountRequest("POST", "/v1/conversations", body, selectedDevice, csrf, key, 16384, false, owner);
                String id = result.optString("conversationId", "");
                if (!isUuid(id)) throw new IOException("invalid_response");
                return id;
            } catch (IOException exception) { failure = exception; }
        }
        throw failure == null ? new IOException("conversation_create_failed") : failure;
    }

    private static boolean validGuestMemory(JSONObject guest) {
        if (!(guest.opt("guestId") instanceof String) || !(guest.opt("identityVerified") instanceof Boolean) ||
            !(guest.opt("longTermAllowed") instanceof Boolean) || !(guest.opt("cloudExportAllowed") instanceof Boolean)) return false;
        String id = guest.optString("guestId", "");
        boolean verified = guest.optBoolean("identityVerified", false);
        boolean longTerm = guest.optBoolean("longTermAllowed", false);
        boolean cloud = guest.optBoolean("cloudExportAllowed", false);
        return id.matches("^[A-Za-z0-9._-]{1,80}$") && verified && (!cloud || longTerm) &&
            guest.length() == 4 && guest.has("identityVerified") && guest.has("longTermAllowed") && guest.has("cloudExportAllowed");
    }

    private static boolean validServerLogPage(JSONObject page) {
        String storeId = page.optString("storeId", "");
        long oldest = page.optLong("oldestCursor", -1), latest = page.optLong("latestCursor", -1), next = page.optLong("nextCursor", -1);
        long dropped = page.optLong("droppedWrites", -1);
        JSONArray entries = page.optJSONArray("entries");
        if (!isUuid(storeId) || oldest < 0 || latest < oldest || latest > 9_007_199_254_740_991L || next < 0 || next > latest || dropped < 0 ||
            entries == null || entries.length() > 200 || !(page.opt("hasMore") instanceof Boolean) || !(page.opt("truncated") instanceof Boolean)) return false;
        long previous = -1;
        for (int index = 0; index < entries.length(); index++) {
            JSONObject entry = entries.optJSONObject(index);
            if (entry == null) return false;
            long id = entry.optLong("id", -1);
            String level = entry.optString("level", "");
            if (id < oldest || id > next || id <= previous || !Arrays.asList("info", "warning", "error").contains(level) ||
                entry.optString("timestamp", "").length() > 64 || entry.optString("eventCode", "").length() > 128) return false;
            previous = id;
        }
        return !page.optBoolean("hasMore") || entries.length() > 0;
    }

    private boolean isLiveReady(JSONObject inventory) {
        try {
            if (inventory.optInt("schemaVersion") != 1 || !"gpt-live".equals(inventory.optString("primaryEngine"))) return false;
            JSONArray list = inventory.getJSONArray("engines");
            for (int index = 0; index < list.length(); index++) {
                JSONObject item = list.getJSONObject(index);
                if ("gpt-live".equals(item.optString("engine"))) return "gpt-live-1".equals(item.optString("model")) && item.optBoolean("ready");
            }
            return false;
        } catch (Exception exception) { return false; }
    }

    private void addCookie(Request.Builder builder) {
        String cookie = CookieManager.getInstance().getCookie(BuildConfig.ACCOUNT_ORIGIN);
        if (cookie != null && !cookie.isEmpty()) builder.header("Cookie", cookie);
    }
    private void persistCookies(Response response) {
        CookieManager manager = CookieManager.getInstance();
        for (String cookie : response.headers("Set-Cookie")) manager.setCookie(BuildConfig.ACCOUNT_ORIGIN, cookie);
        manager.flush();
    }
    private static String problemCode(Response response, String fallback) {
        try { return new JSONObject(response.body() == null ? "{}" : readUtf8(response.body(), 8192)).optString("code", fallback); }
        catch (Exception exception) { return fallback; }
    }
    private static String safeCode(Exception error) {
        String value = error.getMessage();
        return value != null && value.matches("^[a-z][a-z0-9_]{0,63}$") ? value : "account_access_failed";
    }
    private static boolean validDeviceId(String value) { return value != null && !value.isEmpty() && value.length() <= 128 && value.matches("^[A-Za-z0-9_-]+$"); }
    private static boolean isUuid(String value) { return value != null && value.matches("(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"); }
    private void emitStatus() { notifyListeners("state", new JSObject().put("running", active != null || pendingSetup).put("phase", active == null ? "idle" : active.phase)); }

    private final class LiveConversation {
        final PluginCall call;
        final String selectedDevice;
        final int readyMask;
        final String voice;
        final JSONObject guestMemory;
        final String bootId = UUID.randomUUID().toString().toLowerCase(java.util.Locale.ROOT);
        final AtomicBoolean stopped = new AtomicBoolean();
        final AtomicBoolean settled = new AtomicBoolean();
        final AtomicBoolean recording = new AtomicBoolean();
        final LiveSetupCancellation cancellation = new LiveSetupCancellation();
        final List<CompletedConversationSnapshot.TranscriptEntry> transcript = new ArrayList<>();
        volatile String phase = "checking";
        volatile String errorCode;
        volatile String conversationId;
        volatile String streamId;
        volatile String currentInputId;
        volatile WebSocket audioSocket;
        volatile AudioRecord recorder;
        volatile AudioTrack player;
        volatile Thread captureThread;
        volatile boolean outputActive;
        volatile int selected = -1;
        volatile long playedSamples;
        volatile long writtenSamples;
        volatile long turnPcmBytes;
        volatile int responseGeneration;
        volatile int expectedSequence;
        final LiveAudioAcknowledgements acknowledgements = new LiveAudioAcknowledgements();
        final LiveResponseGeneration generation = new LiveResponseGeneration();
        volatile AudioManager audioManager;
        volatile AudioManager.OnAudioFocusChangeListener audioFocusListener;
        volatile String pendingOperation;
        volatile String csrf;
        LiveConversation(PluginCall call, String device, int readyMask, String voice, JSONObject guestMemory) { this.call = call; this.selectedDevice = device; this.readyMask = readyMask; this.voice = voice; this.guestMemory = guestMemory; }

        void bindGuestMemory() throws IOException {
            JSONObject body = json("deviceId", selectedDevice, "conversationId", conversationId, "guestId", guestMemory.optString("guestId"),
                "identitySource", "operator", "identityVerified", true, "longTermAllowed", guestMemory.optBoolean("longTermAllowed"),
                "cloudExportAllowed", guestMemory.optBoolean("cloudExportAllowed"), "expectedVersion", 0);
            requireActive();
            accountRequest("PUT", "/v1/admin/memory/guest", body, selectedDevice, csrf, null, 16384, true, this);
        }

        void start() { network.execute(this::run); }

        void run() {
            boolean terminal = false;
            try {
                requireActive();
                csrf = fetchCsrf(this);
                requireActive();
                JSONObject diagnostics = accountRequest("GET", "/v1/diagnostics", null, selectedDevice, csrf, null, 16384, false, this);
                requireActive();
                if (!isLiveReady(diagnostics.getJSONObject("engines")) || !CATALOG.equals(diagnostics.optString("catalogVersion"))) throw new IOException("gpt_live_unavailable");
                requireActive();
                conversationId = createConversation(selectedDevice, csrf, readyMask, bootId, this);
                requireActive();
                if (guestMemory != null) bindGuestMemory();
                requireActive();
                String openingInputId = UUID.randomUUID().toString().toLowerCase(java.util.Locale.ROOT);
                currentInputId = openingInputId;
                JSONObject body = json("clientTurnId", openingInputId, "bootId", bootId, "generation", 0, "readyMask", readyMask, "deviceRevision", 0, "catalogVersion", CATALOG, "opening", true, "text", "", "wav", JSONObject.NULL, "voice", voice, "speed", 1);
                Request.Builder request = new Request.Builder().url(BuildConfig.ACCOUNT_ORIGIN + "/v1/conversations/" + conversationId + "/gpt-live")
                    .post(RequestBody.create(body.toString(), JSON)).header("Origin", BuildConfig.ACCOUNT_ORIGIN)
                    .header("X-DV-Device-Id", selectedDevice).header("X-DV-CSRF", csrf)
                    .header("Idempotency-Key", UUID.randomUUID().toString().toLowerCase(java.util.Locale.ROOT))
                    .header("Accept", "application/x-ndjson").header("Cache-Control", "no-store");
                addCookie(request);
                try (Response response = execute(this, request.build())) {
                    persistCookies(response);
                    if (response.code() != 200 || response.body() == null || !"application/x-ndjson".equalsIgnoreCase(response.body().contentType() == null ? "" : response.body().contentType().toString().split(";", 2)[0])) {
                        throw new IOException(problemCode(response, "gpt_live_http_failed"));
                    }
                    BufferedSource source = response.body().source();
                    long wireBytes = 0;
                    int events = 0;
                    while (!stopped.get()) {
                        String line;
                        try { line = source.readUtf8LineStrict(65536); }
                        catch (java.io.EOFException eof) { break; }
                        wireBytes += line.getBytes(StandardCharsets.UTF_8).length + 1;
                        if (wireBytes > 20_000_000 || ++events > 20_000) throw new IOException("realtime_live_response_too_large");
                        JSONObject event = new JSONObject(line);
                        terminal = handle(event) || terminal;
                        if ("closed".equals(event.optString("type"))) break;
                    }
                }
                if (!stopped.get() && !terminal) throw new IOException("realtime_live_protocol_failed");
                if (stopped.get()) rejectOnce("Conversation stopped.", "cancelled"); else resolveOnce();
            } catch (Exception exception) {
                if (!stopped.get()) {
                    errorCode = safeLiveCode(exception);
                    rejectOnce("Conversation could not be completed. Check account access, microphone permission, and network connectivity.", errorCode);
                }
            } finally {
                stopAudio();
                if (!settled.get()) rejectOnce("Conversation stopped.", "cancelled");
                main.post(() -> {
                    if (active == this) {
                        if (!stopped.get()) completed.capture(conversationId, transcript, selected, errorCode);
                        active = null;
                    }
                    pendingSetup = false;
                    emitStatus();
                });
            }
        }

        void resolveOnce() { if (settled.compareAndSet(false, true)) call.resolve(); }
        void rejectOnce(String message, String code) { if (settled.compareAndSet(false, true)) call.reject(message, code); }
        void requireActive() throws IOException { cancellation.requireActive(); }

        boolean validCorrelation(JSONObject event) {
            JSONObject correlation = event.optJSONObject("correlation");
            if (correlation == null || correlation.length() != 6) return false;
            return selectedDevice.equals(correlation.optString("deviceId")) && bootId.equals(correlation.optString("bootId")) &&
                conversationId.equals(correlation.optString("conversationId")) && currentInputId.equals(correlation.optString("clientTurnId")) &&
                correlation.optInt("generation", -1) == 0 && correlation.optInt("deviceRevision", -1) == 0;
        }

        boolean handle(JSONObject event) throws Exception {
            String type = event.optString("type", "");
            switch (type) {
                case "session":
                    streamId = event.getString("streamId");
                    if (!isUuid(streamId) || !"pcm16-mono-24000".equals(event.optString("audioFormat"))) throw new IOException("realtime_live_protocol_failed");
                    openAudioSocket(); return false;
                case "listening":
                    String inputId = event.getString("inputId");
                    if (!isUuid(inputId)) throw new IOException("realtime_live_protocol_failed");
                    int announcedGeneration = event.optInt("responseGeneration", generation.current());
                    if (announcedGeneration != generation.current()) throw new IOException("realtime_live_protocol_failed");
                    responseGeneration = generation.current();
                    currentInputId = inputId; expectedSequence = 0; phase = "listening"; startCapture(); emitStatus(); return false;
                case "speech_started": if (!acceptResponseEvent(event)) return false; if (!event.optString("inputId").equals(currentInputId)) throw new IOException("realtime_live_protocol_failed"); phase = "recording"; emitStatus(); return false;
                case "speech_stopped": if (!acceptResponseEvent(event)) return false; if (!event.optString("inputId").equals(currentInputId)) throw new IOException("realtime_live_protocol_failed"); stopCapture(); phase = "thinking"; emitStatus(); return false;
                case "pause": if (!acceptResponseEvent(event)) return false; phase = "listening"; emitStatus(); return false;
                case "heartbeat": return false;
                case "visitor": if (acceptResponseEvent(event)) addTranscript("visitor", event.getString("text")); return false;
                case "started":
                    if (!acceptResponseEvent(event)) return false;
                    pendingOperation = event.getString("operationId");
                    turnPcmBytes = 0;
                    if (!isUuid(pendingOperation) || !validCorrelation(event)) throw new IOException("realtime_live_protocol_failed");
                    return false;
                case "audio":
                    if (!acceptResponseEvent(event)) return false;
                    String encoded = event.getString("delta");
                    if (encoded.length() > 65536) throw new IOException("realtime_live_protocol_failed");
                    byte[] pcm = android.util.Base64.decode(encoded, android.util.Base64.DEFAULT);
                    if (pcm.length == 0 || pcm.length > 49152 || pcm.length % 2 != 0) throw new IOException("realtime_live_protocol_failed");
                    turnPcmBytes += pcm.length;
                    if (turnPcmBytes > 9_120_000) throw new IOException("realtime_live_response_too_large");
                    writePcm(pcm); return false;
                case "audio_cancel":
                    int nextGeneration = event.getInt("responseGeneration");
                    if (nextGeneration < 1 || !generation.advance(nextGeneration)) throw new IOException("realtime_live_protocol_failed");
                    responseGeneration = generation.current();
                    turnPcmBytes = 0;
                    AudioTrack currentPlayer = player;
                    if (currentPlayer != null) { currentPlayer.pause(); currentPlayer.flush(); currentPlayer.play(); writtenSamples = 0; playedSamples = 0; }
                    return false;
                case "playback_checkpoint":
                    if (!acceptResponseEvent(event)) return false;
                    String playbackId = event.getString("playbackId");
                    int checkpointBytes = event.getInt("pcmBytes");
                    if (!isUuid(playbackId) || checkpointBytes <= 0 || checkpointBytes != turnPcmBytes) throw new IOException("realtime_live_protocol_failed");
                    JSONObject checkpoint = json("playbackId", playbackId, "pcmBytes", checkpointBytes, "drained", true);
                    drainPlayback();
                    requireActive();
                    accountRequest("POST", "/v1/conversations/" + conversationId + "/gpt-live/" + streamId + "/playback",
                        checkpoint, selectedDevice, csrf, null, 4096, false, this);
                    return false;
                case "completed":
                    if (!acceptResponseEvent(event)) return false;
                    JSONObject result = event.getJSONObject("result");
                    String reply = result.optString("reply", "");
                    String decision = result.optString("decision", "");
                    if (reply.isEmpty() || reply.length() > 500 || !Arrays.asList("ask", "propose", "no_selection").contains(decision)) throw new IOException("realtime_live_protocol_failed");
                    JSONObject proposal = result.optJSONObject("proposal");
                    int compartment = proposal == null ? 0 : proposal.optInt("compartment", 0);
                    if ("propose".equals(decision) && (proposal == null || !conversationId.equals(proposal.optString("conversationId")) || !currentInputId.equals(proposal.optString("turnId")) || !bootId.equals(proposal.optString("bootId")) || !CATALOG.equals(proposal.optString("catalogVersion")) || proposal.optInt("generation", -1) != 0 || proposal.optInt("deviceRevision", -1) != 0 || compartment < 1 || compartment > 6 || (readyMask & (1 << (compartment - 1))) == 0)) throw new IOException("realtime_live_protocol_failed");
                    if (!"propose".equals(decision) && proposal != null) throw new IOException("realtime_live_protocol_failed");
                    String operationId = event.getString("operationId");
                    if (!isUuid(operationId) || !operationId.equals(pendingOperation) || !validCorrelation(event)) throw new IOException("realtime_live_protocol_failed");
                    if ("propose".equals(decision)) selected = compartment;
                    JSONObject completion = json("operationId", operationId, "responseGeneration", event.optInt("responseGeneration", responseGeneration));
                    requireActive();
                    accountRequest("POST", "/v1/conversations/" + conversationId + "/gpt-live/" + streamId + "/played",
                        completion, selectedDevice, csrf, null, 4096, false, this);
                    pendingOperation = null;
                    addTranscript("kufr", reply);
                    return false;
                case "closed":
                    if (!"conversation_terminal".equals(event.optString("reason"))) throw new IOException("realtime_live_protocol_failed");
                    phase = "completed"; return true;
                case "failed": throw new IOException(event.optString("code", "gpt_live_failed"));
                default: throw new IOException("realtime_live_protocol_failed");
            }
        }

        boolean acceptResponseEvent(JSONObject event) throws IOException {
            int incoming = event.optInt("responseGeneration", -1);
            LiveResponseGeneration.Decision decision = generation.accepts(incoming);
            if (decision == LiveResponseGeneration.Decision.INVALID) throw new IOException("realtime_live_protocol_failed");
            return decision == LiveResponseGeneration.Decision.ACCEPT;
        }

        void openAudioSocket() throws IOException {
            Request.Builder request = new Request.Builder().url(BuildConfig.ACCOUNT_ORIGIN.replaceFirst("^https:", "wss:") +
                "/v1/conversations/" + conversationId + "/gpt-live/" + streamId + "/audio-stream").get()
                .header("Origin", BuildConfig.ACCOUNT_ORIGIN).header("X-DV-Device-Id", selectedDevice).header("X-DV-CSRF", csrf);
            addCookie(request);
            audioSocket = client.newWebSocket(request.build(), new WebSocketListener() {
                @Override public void onMessage(@NonNull WebSocket socket, @NonNull String text) {
                    try {
                        JSONObject ack = new JSONObject(text);
                        if (!"ack".equals(ack.optString("type")) || !acknowledgements.acknowledge(ack.optString("inputId"), ack.optInt("sequence", -1))) {
                            stop("realtime_live_protocol_failed"); return;
                        }
                    } catch (Exception exception) { stop("realtime_live_protocol_failed"); }
                }
                @Override public void onFailure(@NonNull WebSocket socket, @NonNull Throwable error, Response response) {
                    if (!stopped.get()) stop("realtime_live_connection_failed");
                }
            });
        }

        void startCapture() throws IOException {
            if (recording.get() || audioSocket == null) return;
            acquireAudioFocus();
            int sourceRate = 48000;
            int minBytes = AudioRecord.getMinBufferSize(sourceRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (minBytes <= 0) throw new IOException("realtime_live_audio_start_failed");
            if (getContext().checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) throw new IOException("microphone_unavailable");
            AudioRecord record;
            try {
                record = new AudioRecord(MediaRecorder.AudioSource.VOICE_COMMUNICATION, sourceRate,
                    AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(minBytes, 38400));
            } catch (SecurityException exception) { throw new IOException("microphone_unavailable", exception); }
            if (record.getState() != AudioRecord.STATE_INITIALIZED) { record.release(); throw new IOException("realtime_live_audio_start_failed"); }
            try { record.startRecording(); }
            catch (SecurityException exception) { record.release(); throw new IOException("microphone_unavailable", exception); }
            recorder = record; recording.set(true);
            captureThread = new Thread(() -> capture(record, sourceRate), "wand-live-microphone");
            captureThread.start();
        }

        void capture(AudioRecord record, int sourceRate) {
            short[] source = new short[sourceRate / 5]; // 200 ms at 48 kHz
            while (recording.get() && recorder == record && !stopped.get()) {
                int count = record.read(source, 0, source.length, AudioRecord.READ_BLOCKING);
                if (count <= 0) { if (!stopped.get() && recorder == record) stop("realtime_live_audio_failed"); return; }
                int outputCount = count * PCM_RATE / sourceRate;
                byte[] pcm = new byte[outputCount * 2];
                for (int index = 0; index < outputCount; index++) {
                    int from = index * 2;
                    if (from + 1 >= count) break;
                    short sample = (short) (((int) source[from] + (int) source[from + 1]) / 2);
                    pcm[index * 2] = (byte) (sample & 0xff);
                    pcm[index * 2 + 1] = (byte) ((sample >>> 8) & 0xff);
                }
                String input = currentInputId;
                int sequence = expectedSequence++;
                WebSocket socket = audioSocket;
                long sentAt = android.os.SystemClock.elapsedRealtime();
                if (input == null || socket == null || socket.queueSize() > 192_000 || !acknowledgements.enqueue(input, sequence, pcm.length, sentAt)) { stop("realtime_live_audio_overflow"); return; }
                try {
                    JSONObject frame = json("inputId", input, "sequence", sequence, "audio", android.util.Base64.encodeToString(pcm, android.util.Base64.NO_WRAP));
                    if (!socket.send(frame.toString())) { stop("realtime_live_audio_failed"); return; }
                    main.postDelayed(() -> {
                        if (!stopped.get() && acknowledgements.timedOut(android.os.SystemClock.elapsedRealtime())) stop("realtime_live_connection_timeout");
                    }, 10_000);
                } catch (Exception exception) { stop("realtime_live_audio_failed"); return; }
            }
        }

        void stopCapture() {
            recording.set(false);
            AudioRecord record = recorder; recorder = null;
            if (record != null) { try { record.stop(); } catch (Exception ignored) { } record.release(); }
            Thread capture = captureThread; captureThread = null;
            if (capture != null && capture != Thread.currentThread()) {
                capture.interrupt();
                try { capture.join(1000); } catch (InterruptedException exception) { Thread.currentThread().interrupt(); }
            }
        }

        void writePcm(byte[] pcm) throws IOException {
            acquireAudioFocus();
            AudioTrack track = player;
            if (track == null) {
                int min = AudioTrack.getMinBufferSize(PCM_RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT);
                AudioFormat format = new AudioFormat.Builder().setSampleRate(PCM_RATE).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build();
                AudioAttributes attributes = new AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build();
                track = new AudioTrack.Builder().setAudioAttributes(attributes).setAudioFormat(format).setBufferSizeInBytes(Math.max(min, 19200)).setTransferMode(AudioTrack.MODE_STREAM).build();
                if (track.getState() != AudioTrack.STATE_INITIALIZED) { track.release(); throw new IOException("realtime_live_audio_start_failed"); }
                track.play(); player = track;
            }
            outputActive = true;
            int offset = 0;
            while (offset < pcm.length && !stopped.get()) {
                int written = track.write(pcm, offset, pcm.length - offset, AudioTrack.WRITE_BLOCKING);
                if (written <= 0) throw new IOException("realtime_live_audio_failed");
                offset += written; writtenSamples += written / 2;
            }
        }

        void drainPlayback() throws IOException {
            AudioTrack track = player;
            if (track == null) return;
            long target = writtenSamples;
            long deadline = android.os.SystemClock.elapsedRealtime() + 10000;
            while (!stopped.get() && android.os.SystemClock.elapsedRealtime() < deadline) {
                long played = Integer.toUnsignedLong(track.getPlaybackHeadPosition());
                if (played >= target) { playedSamples = played; outputActive = false; return; }
                try { Thread.sleep(10); } catch (InterruptedException exception) { Thread.currentThread().interrupt(); throw new IOException("cancelled", exception); }
            }
            throw new IOException("realtime_live_audio_playback_timeout");
        }

        void addTranscript(String role, String text) {
            if (text == null || text.length() > 1000) return;
            synchronized (transcript) {
                while (transcript.size() >= 200) transcript.remove(0);
                transcript.add(new CompletedConversationSnapshot.TranscriptEntry(role, text));
            }
            emitStatus();
        }

        List<CompletedConversationSnapshot.TranscriptEntry> transcriptSnapshot() {
            synchronized (transcript) { return new ArrayList<>(transcript); }
        }

        void stop(String code) {
            if (!stopped.compareAndSet(false, true)) return;
            cancellation.stop();
            errorCode = code;
            rejectOnce("Conversation stopped.", code);
            stopCapture();
            WebSocket socket = audioSocket; if (socket != null) socket.close(1000, null);
            stopAudio();
            main.post(() -> { phase = "cancelled"; emitStatus(); });
        }

        synchronized void stopAudio() {
            stopCapture();
            WebSocket socket = audioSocket; audioSocket = null; if (socket != null) socket.close(1000, null);
            AudioTrack track = player; player = null;
            if (track != null) { try { track.pause(); track.flush(); track.stop(); } catch (Exception ignored) { } track.release(); }
            AudioManager manager = audioManager;
            AudioManager.OnAudioFocusChangeListener listener = audioFocusListener;
            if (manager != null && listener != null) manager.abandonAudioFocus(listener);
            audioManager = null; audioFocusListener = null;
            outputActive = false;
        }

        void acquireAudioFocus() throws IOException {
            if (audioManager != null) return;
            AudioManager manager = (AudioManager) getContext().getSystemService(Context.AUDIO_SERVICE);
            if (manager == null) throw new IOException("realtime_live_audio_start_failed");
            AudioManager.OnAudioFocusChangeListener listener = change -> {
                if (AudioFocusLossPolicy.shouldStop(change)) stop("realtime_live_audio_interrupted");
            };
            int result = manager.requestAudioFocus(listener, AudioManager.STREAM_VOICE_CALL, AudioManager.AUDIOFOCUS_GAIN_TRANSIENT_EXCLUSIVE);
            if (result != AudioManager.AUDIOFOCUS_REQUEST_GRANTED) throw new IOException("realtime_live_audio_start_failed");
            audioManager = manager; audioFocusListener = listener;
        }
    }

    private static String safeLiveCode(Exception exception) {
        String code = exception.getMessage();
        if (code != null && code.matches("^[a-z][a-z0-9_]{0,63}$")) return code;
        if (exception instanceof javax.net.ssl.SSLException) return "realtime_live_tls_failed";
        if (exception instanceof java.net.SocketTimeoutException) return "realtime_live_connection_timeout";
        return "realtime_live_failed";
    }
}
