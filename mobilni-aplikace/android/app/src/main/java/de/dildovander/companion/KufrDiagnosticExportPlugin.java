package de.dildovander.companion;

import android.content.Intent;
import android.net.Uri;
import android.util.Log;

import androidx.core.content.FileProvider;

import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.JSObject;
import com.getcapacitor.annotation.CapacitorPlugin;

import org.json.JSONTokener;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.io.FileOutputStream;
import java.util.UUID;
import java.util.regex.Pattern;

@CapacitorPlugin(name = "KufrDiagnosticExport")
public final class KufrDiagnosticExportPlugin extends Plugin {
    private static final int MAX_BYTES = 1_048_576;
    private static final Pattern FILE_NAME = Pattern.compile("^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\\.json$");

    @PluginMethod
    public void export(PluginCall call) {
        String name = call.getString("fileName", "");
        String contents = call.getString("contents", "");
        byte[] bytes = contents.getBytes(StandardCharsets.UTF_8);
        if (!FILE_NAME.matcher(name).matches() || name.contains("..") || bytes.length > MAX_BYTES || !isJson(contents)) {
            call.reject("The diagnostic report is invalid or too large.", "diagnostic_export_unavailable");
            return;
        }
        File directory = new File(new File(getContext().getCacheDir(), "diagnostics"), "KufrDiagnostic-" + UUID.randomUUID());
        if (!directory.mkdirs()) {
            call.reject("The diagnostic report could not be written.", "diagnostic_export_write_failed");
            return;
        }
        File file = new File(directory, name);
        try {
            try (FileOutputStream output = new FileOutputStream(file)) { output.write(bytes); }
            Uri uri = FileProvider.getUriForFile(getContext(), getContext().getPackageName() + ".fileprovider", file);
            Intent share = new Intent(Intent.ACTION_SEND);
            share.setType("application/json");
            share.putExtra(Intent.EXTRA_STREAM, uri);
            share.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            share.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            getContext().startActivity(Intent.createChooser(share, "Share diagnostic report").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            JSObject result = new JSObject();
            result.put("status", "shared");
            call.resolve(result);
        } catch (Exception exception) {
            Log.w("KufrDiagnosticExport", "Unable to share diagnostic report");
            deleteTree(directory);
            call.reject("The diagnostic report could not be shared.", "diagnostic_export_share_failed");
        }
    }

    private static boolean isJson(String value) {
        try {
            Object parsed = new JSONTokener(value).nextValue();
            return parsed instanceof org.json.JSONObject || parsed instanceof org.json.JSONArray;
        } catch (Exception exception) { return false; }
    }

    private static void deleteTree(File file) {
        File[] children = file.listFiles();
        if (children != null) for (File child : children) deleteTree(child);
        file.delete();
    }
}
