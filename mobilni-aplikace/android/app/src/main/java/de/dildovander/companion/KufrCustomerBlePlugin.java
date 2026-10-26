package de.dildovander.companion;

import android.Manifest;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;

import androidx.annotation.NonNull;

import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import com.getcapacitor.annotation.Permission;

import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;

@CapacitorPlugin(name = "KufrCustomerBle", permissions = {
    @Permission(alias = "bluetooth", strings = { Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT }),
    @Permission(alias = "location", strings = { Manifest.permission.ACCESS_FINE_LOCATION })
})
public final class KufrCustomerBlePlugin extends Plugin {
    private static final UUID SERVICE = UUID.fromString("B97A7F10-70E8-4B98-9DCC-40D66395C822");
    private static final UUID CONTROL = UUID.fromString("B97AFF55-70E8-4B98-9DCC-40D66395C822");
    private static final int MAX_BYTES = 512;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Map<String, BluetoothDevice> discovered = new HashMap<>();
    private BluetoothAdapter adapter;
    private BluetoothLeScanner scanner;
    private BluetoothGatt gatt;
    private BluetoothGattCharacteristic characteristic;
    private PluginCall pending;
    private String operationId;
    private Runnable timeout;
    private boolean scanning;

    @Override
    public void load() {
        BluetoothManager manager = (BluetoothManager) getContext().getSystemService(android.content.Context.BLUETOOTH_SERVICE);
        adapter = manager == null ? null : manager.getAdapter();
    }

    @PluginMethod
    public void discover(PluginCall call) {
        if (!ensurePermissions(call)) return;
        begin(call, 12000);
        if (pending != call) return;
        if (adapter == null || !adapter.isEnabled()) { finishError("bluetooth_off"); return; }
        scanner = adapter.getBluetoothLeScanner();
        if (scanner == null) { finishError("unsupported"); return; }
        discovered.clear();
        scanning = true;
        try {
            ScanSettings settings = new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build();
            scanner.startScan(null, settings, scanCallback);
            handler.postDelayed(() -> {
                if (!scanning) return;
                stopScan();
                ArrayList<JSObject> entries = new ArrayList<>();
                discovered.entrySet().stream().sorted(Map.Entry.comparingByKey()).forEach(entry -> {
                    JSObject device = new JSObject();
                    device.put("id", entry.getKey());
                    device.put("name", entry.getValue().getName() == null ? "Wand Night Stand" : entry.getValue().getName());
                    entries.add(device);
                });
                JSArray result = new JSArray();
                for (JSObject entry : entries) result.put(entry);
                JSObject value = new JSObject(); value.put("devices", result); finish(value);
            }, 5000);
        } catch (SecurityException exception) { finishError("permission_denied"); }
    }

    private final ScanCallback scanCallback = new ScanCallback() {
        @Override public void onScanResult(int callbackType, ScanResult result) {
            if (!scanning || result.getScanRecord() == null) return;
            if (result.getScanRecord().getServiceUuids() == null || result.getScanRecord().getServiceUuids().stream().noneMatch(uuid -> SERVICE.equals(uuid.getUuid()))) return;
            BluetoothDevice device = result.getDevice();
            try { if (device != null && discovered.size() < 32) discovered.put(device.getAddress(), device); }
            catch (SecurityException ignored) { finishError("permission_denied"); }
        }
        @Override public void onScanFailed(int errorCode) { finishError(errorCode == 2 ? "bluetooth_off" : "scan_failed"); }
    };

    @PluginMethod
    public void connect(PluginCall call) {
        if (!ensurePermissions(call)) return;
        String id = call.getString("id", "");
        BluetoothDevice selected = discovered.get(id);
        if (selected == null) { call.reject("Device was not discovered.", "device_not_discovered"); return; }
        begin(call, 15000);
        if (pending != call) return;
        stopScan();
        operationId = null;
        disconnectGatt();
        try {
            gatt = selected.connectGatt(getContext(), false, gattCallback, BluetoothDevice.TRANSPORT_LE);

            if (gatt == null) finishError("connection_failed");
        } catch (SecurityException exception) { finishError("permission_denied"); }
    }

    private final BluetoothGattCallback gattCallback = new BluetoothGattCallback() {
        @Override public void onConnectionStateChange(@NonNull BluetoothGatt source, int status, int newState) {
            if (source != gatt || pending == null) return;
            if (status != BluetoothGatt.GATT_SUCCESS) { finishError("connection_failed"); return; }
            if (newState == BluetoothProfile.STATE_CONNECTED) {
                try {
                    if (!hasBluetoothPermissions()) { finishError("permission_denied"); return; }
                    if (!source.discoverServices()) finishError("service_discovery_failed");
                } catch (SecurityException exception) { finishError("permission_denied"); }
            }
            else if (newState == BluetoothProfile.STATE_DISCONNECTED) finishError("disconnected");
        }
        @Override public void onServicesDiscovered(@NonNull BluetoothGatt source, int status) {
            if (source != gatt || pending == null) return;
            if (!hasBluetoothPermissions()) { finishError("permission_denied"); return; }
            try {
                BluetoothGattService service = source.getService(SERVICE);
                BluetoothGattCharacteristic selected = service == null ? null : service.getCharacteristic(CONTROL);
                if (status != BluetoothGatt.GATT_SUCCESS || selected == null ||
                    (selected.getProperties() & BluetoothGattCharacteristic.PROPERTY_READ) == 0 ||
                    (selected.getProperties() & BluetoothGattCharacteristic.PROPERTY_WRITE) == 0) {
                    finishError("requires_update"); return;
                }
                characteristic = selected;
                send(request("challenge", null, null));
            } catch (SecurityException exception) { finishError("permission_denied"); }
        }
        @Override public void onCharacteristicWrite(@NonNull BluetoothGatt source, @NonNull BluetoothGattCharacteristic target, int status) {
            if (source != gatt || target != characteristic || pending == null) return;
            if (status != BluetoothGatt.GATT_SUCCESS) { finishError("write_failed"); return; }
            try { source.readCharacteristic(target); }
            catch (SecurityException exception) { finishError("permission_denied"); }
        }
        @Override public void onCharacteristicRead(@NonNull BluetoothGatt source, @NonNull BluetoothGattCharacteristic target, byte[] value, int status) {
            handleRead(source, target, value, status);
        }
        @SuppressWarnings("deprecation")
        @Override public void onCharacteristicRead(@NonNull BluetoothGatt source, @NonNull BluetoothGattCharacteristic target, int status) {
            handleRead(source, target, target.getValue(), status);
        }
        private void handleRead(BluetoothGatt source, BluetoothGattCharacteristic target, byte[] value, int status) {
            if (source != gatt || target != characteristic || pending == null) return;
            if (!hasBluetoothPermissions()) { finishError("permission_denied"); return; }
            if (status != BluetoothGatt.GATT_SUCCESS || value == null) { finishError("invalid_response"); return; }
            try {
                JSONObject object = new JSONObject(new String(value, StandardCharsets.UTF_8));
                if (!validCustomerResponse(object)) { finishError("invalid_response"); return; }
                JSObject result = JSObject.fromJSONObject(object);
                finish(result);
            } catch (Exception exception) { finishError("invalid_response"); }
        }
    };

    @PluginMethod
    public void start(PluginCall call) {
        String ticket = call.getString("ticket", "");
        String id = call.getString("operationId", "");
        if (!validTicket(ticket) || !isUuid(id)) { call.reject("Invalid request.", "invalid_request"); return; }
        if (characteristic == null || gatt == null) { call.reject("Bluetooth connection is unavailable.", "unavailable"); return; }
        begin(call, 15000);
        if (pending != call) return;
        operationId = id;
        send(request("start", ticket, id));
    }

    @PluginMethod public void status(PluginCall call) { query(call, "status"); }
    @PluginMethod public void end(PluginCall call) { query(call, "end"); }

    private void query(PluginCall call, String operation) {
        if (characteristic == null || gatt == null) { call.reject("Bluetooth connection is unavailable.", "unavailable"); return; }
        begin(call, 15000);
        if (pending != call) return;
        String id = call.getString("operationId", operationId);
        JSONObject object = request(operation, null, id);
        send(object);
    }

    @PluginMethod
    public void disconnect(PluginCall call) {
        stopScan();
        operationId = null;
        finishError("cancelled");
        disconnectGatt();
        call.resolve();
    }

    private boolean hasBluetoothPermissions() {
        if (Build.VERSION.SDK_INT < 31) return getContext().checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED;
        return getContext().checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED &&
            getContext().checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) == PackageManager.PERMISSION_GRANTED;
    }

    private void send(JSONObject object) {
        if (!hasBluetoothPermissions()) { finishError("permission_denied"); return; }
        try {
            byte[] bytes = object.toString().getBytes(StandardCharsets.UTF_8);
            if (bytes.length > MAX_BYTES || gatt == null || characteristic == null) { finishError("unavailable"); return; }
            int result;
            if (Build.VERSION.SDK_INT >= 33) result = gatt.writeCharacteristic(characteristic, bytes, BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT);
            else {
                characteristic.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT);
                characteristic.setValue(bytes);
                result = gatt.writeCharacteristic(characteristic) ? BluetoothGatt.GATT_SUCCESS : BluetoothGatt.GATT_FAILURE;
            }
            if (result != BluetoothGatt.GATT_SUCCESS) finishError("write_failed");
        } catch (SecurityException exception) { finishError("permission_denied"); }
        catch (Exception exception) { finishError("unavailable"); }
    }
    private boolean ensurePermissions(PluginCall call) {
        String alias = Build.VERSION.SDK_INT >= 31 ? "bluetooth" : "location";
        String[] required = Build.VERSION.SDK_INT >= 31
            ? new String[] { Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT }
            : new String[] { Manifest.permission.ACCESS_FINE_LOCATION };
        for (String permission : required) {
            if (getContext().checkSelfPermission(permission) != PackageManager.PERMISSION_GRANTED) {
                requestPermissionForAlias(alias, call, "permissionResult");
                return false;
            }
        }
        return true;
    }

    @com.getcapacitor.annotation.PermissionCallback
    private void permissionResult(PluginCall call) {
        String alias = Build.VERSION.SDK_INT >= 31 ? "bluetooth" : "location";
        if (getPermissionState(alias) != com.getcapacitor.PermissionState.GRANTED) {
            call.reject("Bluetooth permission was denied.", "permission_denied");
            return;
        }
        if ("discover".equals(call.getMethodName())) discover(call);
        else connect(call);
    }

    private void begin(PluginCall call, long timeoutMs) {
        if (pending != null) { call.reject("Another Bluetooth request is active.", "busy"); return; }
        pending = call;
        timeout = () -> finishError("timeout");
        handler.postDelayed(timeout, timeoutMs);
    }
    private void finish(JSObject result) {
        PluginCall current = pending;
        pending = null;
        if (timeout != null) handler.removeCallbacks(timeout);
        timeout = null;
        if (current != null) current.resolve(result);
    }
    private void finishError(String code) {
        PluginCall current = pending;
        pending = null;
        if (timeout != null) handler.removeCallbacks(timeout);
        timeout = null;
        if (current != null) current.reject(code, code);
    }
    private void stopScan() {
        scanning = false;
        if (scanner != null) try { scanner.stopScan(scanCallback); } catch (SecurityException ignored) { }
    }
    private void disconnectGatt() {
        BluetoothGatt old = gatt;
        gatt = null;
        characteristic = null;
        if (old != null) {
            try { old.disconnect(); old.close(); } catch (SecurityException ignored) { }
        }
    }
    private static boolean validCustomerResponse(JSONObject object) {
        try {
            java.util.Set<String> fields = new java.util.HashSet<>(java.util.Arrays.asList("v", "deviceId", "bootId", "challenge", "state", "capabilities", "operationId", "sessionId", "error"));
            java.util.Iterator<String> keys = object.keys();
            while (keys.hasNext()) if (!fields.contains(keys.next())) return false;
            if (object.getInt("v") != 1 || object.getString("deviceId").isEmpty() || object.getString("deviceId").length() > 128 ||
                !isUuid(object.getString("bootId")) || (!object.optString("challenge").isEmpty() && !isUuid(object.optString("challenge"))) ||
                !new java.util.HashSet<>(java.util.Arrays.asList("idle", "starting", "running", "recovering", "completed", "failed")).contains(object.getString("state"))) return false;
            JSONObject capabilities = object.getJSONObject("capabilities");
            Object supported = capabilities.get("customerStart");
            if (capabilities.length() != 1 || !(supported instanceof Number) || (((Number) supported).doubleValue() != 0 && ((Number) supported).doubleValue() != 1)) return false;
            for (String key : new String[] { "operationId", "sessionId" }) if (object.has(key) && !isUuid(object.getString(key))) return false;
            return !object.has("error") || object.getString("error").length() <= 96;
        } catch (Exception exception) { return false; }
    }
    private static boolean validTicket(String value) { return value.length() > 0 && value.getBytes(StandardCharsets.UTF_8).length <= 256 && value.matches("^[A-Za-z0-9_-]+$"); }
    private static boolean isUuid(String value) { return value != null && value.matches("(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"); }
    private static JSONObject request(String operation, String ticket, String id) {
        try {
            JSONObject value = new JSONObject().put("v", 1).put("op", operation);
            if (ticket != null) value.put("ticket", ticket);
            if (id != null) value.put("operationId", id);
            return value;
        } catch (org.json.JSONException exception) { throw new IllegalStateException(exception); }
    }
}
