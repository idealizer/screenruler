import Carbon.HIToolbox
import Foundation

let screenRulerShowNote = Notification.Name("ch.idealizer.ScreenRuler.show")

final class SummonHotKey {
    private var hotKey: EventHotKeyRef?
    private var handler: EventHandlerRef?

    func install() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            DispatchQueue.main.async {
                NotificationCenter.default.post(name: screenRulerShowNote, object: nil)
                DistributedNotificationCenter.default().post(name: screenRulerShowNote, object: nil)
            }
            return noErr
        }, 1, &spec, nil, &handler)
        let id = EventHotKeyID(signature: OSType(0x5352_4C52), id: 1)
        RegisterEventHotKey(
            UInt32(kVK_ANSI_R),
            UInt32(cmdKey | optionKey),
            id,
            GetApplicationEventTarget(),
            0,
            &hotKey
        )
    }
}
