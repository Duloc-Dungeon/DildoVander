import Capacitor
import UIKit

@objc(KufrDiagnosticExportPlugin)
public final class KufrDiagnosticExportPlugin: CAPPlugin, CAPBridgedPlugin {
    public let identifier = "KufrDiagnosticExportPlugin"
    public let jsName = "KufrDiagnosticExport"
    public let pluginMethods = [CAPPluginMethod(name: "export", returnType: CAPPluginReturnPromise)]

    @objc func export(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let fileName = call.getString("fileName"),
                  let contents = call.getString("contents"),
                  let controller = self.bridge?.viewController,
                  controller.viewIfLoaded?.window != nil,
                  controller.presentedViewController == nil else {
                call.reject("The diagnostic export is unavailable.", "diagnostic_export_unavailable")
                return
            }

            let file: URL
            do {
                file = try DiagnosticExportFile.write(fileName: fileName, contents: contents)
            } catch {
                call.reject("The diagnostic report could not be written.", "diagnostic_export_write_failed")
                return
            }

            let share = UIActivityViewController(activityItems: [file], applicationActivities: nil)
            var finished = false
            let finish: (String?) -> Void = { status in
                guard !finished else { return }
                finished = true
                try? FileManager.default.removeItem(at: file.deletingLastPathComponent())
                if let status {
                    call.resolve(["status": status])
                } else {
                    call.reject("The diagnostic report could not be shared.", "diagnostic_export_share_failed")
                }
            }
            share.completionWithItemsHandler = { _, completed, _, activityError in
                if activityError != nil {
                    finish(nil)
                } else {
                    finish(completed ? "shared" : "cancelled")
                }
            }
            if let popover = share.popoverPresentationController {
                popover.sourceView = controller.view
                popover.sourceRect = CGRect(x: controller.view.bounds.midX, y: controller.view.bounds.midY, width: 1, height: 1)
            }
            controller.present(share, animated: true) {
                if controller.presentedViewController !== share {
                    finish(nil)
                }
            }
        }
    }
}
