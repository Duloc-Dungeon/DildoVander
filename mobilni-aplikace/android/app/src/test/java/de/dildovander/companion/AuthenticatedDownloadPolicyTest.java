package de.dildovander.companion;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public final class AuthenticatedDownloadPolicyTest {
    @Test public void validatesShareMimeAgainstTargetExtension() {
        assertTrue(KufrIdentityPlugin.validAuthenticatedDownloadMime("application/zip", "/downloads/releases/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/model.zip"));
        assertTrue(KufrIdentityPlugin.validAuthenticatedDownloadMime("model/stl", "/downloads/releases/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/model.stl"));
        assertFalse(KufrIdentityPlugin.validAuthenticatedDownloadMime("text/html", "/downloads/releases/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/model.zip"));
        assertFalse(KufrIdentityPlugin.validAuthenticatedDownloadMime("application/pdf", "/downloads/releases/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/model.zip"));
    }
}
