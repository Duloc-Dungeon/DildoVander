package de.dildovander.companion;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;

import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import org.junit.runner.RunWith;

@RunWith(AndroidJUnit4.class)
public final class AndroidSmokeTest {
    @Test
    public void nativeShellLoadsWithTheExpectedIdentityAndBridges() {
        assertEquals("de.dildovander.companion", InstrumentationRegistry.getInstrumentation().getTargetContext().getPackageName());
        try (ActivityScenario<MainActivity> scenario = ActivityScenario.launch(MainActivity.class)) {
            scenario.onActivity(activity -> {
                assertNotNull(activity.getBridge());
                assertNotNull(activity.getBridge().getWebView());
                for (String plugin : new String[] { "KufrIdentity", "KufrDebug", "KufrDeviceTransport", "KufrCustomerBle", "KufrDiagnosticExport" }) {
                    assertNotNull("Missing native bridge: " + plugin, activity.getBridge().getPlugin(plugin));
                }
            });
            scenario.moveToState(androidx.lifecycle.Lifecycle.State.CREATED);
            scenario.moveToState(androidx.lifecycle.Lifecycle.State.RESUMED);
        }
    }
}
