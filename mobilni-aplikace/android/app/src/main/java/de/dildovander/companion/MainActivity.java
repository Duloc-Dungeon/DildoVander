package de.dildovander.companion;

import com.getcapacitor.BridgeActivity;

public class MainActivity extends BridgeActivity {
    @Override
    public void onCreate(android.os.Bundle savedInstanceState) {
        registerPlugin(KufrDeviceTransportPlugin.class);
        registerPlugin(KufrCustomerBlePlugin.class);
        registerPlugin(KufrIdentityPlugin.class);
        registerPlugin(KufrDiagnosticExportPlugin.class);
        registerPlugin(KufrDebugPlugin.class);
        super.onCreate(savedInstanceState);
    }
}
