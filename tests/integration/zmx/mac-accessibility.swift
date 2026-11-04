// Test-only driver for the isolated pinned Mac viewer. Never posts global input.
import AppKit
import ApplicationServices

func fail(_ message: String) -> Never {
    fputs(message + "\n", stderr)
    exit(1)
}
let args = CommandLine.arguments
guard args.count == 5, let pid = Int32(args[1]) else { fail("expected PID bundle action text") }
let bundle = URL(fileURLWithPath: args[2]).resolvingSymlinksInPath()
guard bundle.lastPathComponent == "agterm.app",
      bundle.deletingLastPathComponent().lastPathComponent.hasPrefix("aw-stage3-viewer-"),
      bundle.deletingLastPathComponent().deletingLastPathComponent().path == URL(fileURLWithPath: "/tmp").resolvingSymlinksInPath().path,
      let running = NSRunningApplication(processIdentifier: pid) else { fail("isolated viewer path or PID invalid: " + bundle.path) }
guard running.bundleURL?.resolvingSymlinksInPath().path == bundle.path else {
    fail("isolated viewer bundle mismatch: expected \(bundle.path), got \(String(describing: running.bundleURL))")
}
guard AXIsProcessTrusted() else { fail("Accessibility permission missing for test driver") }
guard let session = CGSessionCopyCurrentDictionary() as? [String: Any],
      session["CGSSessionScreenIsLocked"] as? Bool != true else { fail("unlock the Mac before running Accessibility tests") }
let app = AXUIElementCreateApplication(pid)
AXUIElementSetMessagingTimeout(app, 3)
func value(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var result: CFTypeRef?
    guard AXUIElementCopyAttributeValue(element, name as CFString, &result) == .success else { return nil }
    return result
}
func string(_ element: AXUIElement, _ name: String) -> String { value(element, name) as? String ?? "" }
func elements() -> [AXUIElement] {
    var queue = value(app, "AXWindows") as? [AXUIElement] ?? []
    var result: [AXUIElement] = []
    while !queue.isEmpty && result.count < 1000 {
        let element = queue.removeFirst()
        if result.contains(where: { CFEqual($0, element) }) { continue }
        result.append(element)
        queue += value(element, "AXChildren") as? [AXUIElement] ?? []
    }
    return result
}
func dump() -> String {
    let rows = elements().map { element in
        Dictionary(uniqueKeysWithValues: ["AXRole", "AXIdentifier", "AXTitle", "AXDescription", "AXValue", "AXFocused"].map {
            ($0, String(describing: value(element, $0) ?? "" as CFString).prefix(200).description)
        })
    }
    return String(data: try! JSONSerialization.data(withJSONObject: rows, options: [.sortedKeys]), encoding: .utf8)!
}
func unique(_ predicate: (AXUIElement) -> Bool) -> AXUIElement {
    let found = elements().filter(predicate)
    guard found.count == 1 else { fail("expected unique AX target, found \(found.count); inspect saved AX tree") }
    return found[0]
}
func identified(_ id: String) -> AXUIElement { unique { string($0, "AXIdentifier") == id } }
func press(_ id: String) {
    guard AXUIElementPerformAction(identified(id), "AXPress" as CFString) == .success else { fail("AXPress failed: " + id) }
    Thread.sleep(forTimeInterval: 0.2)
}
func key(_ code: CGKeyCode, text: String? = nil) {
    guard !running.isTerminated else { fail("viewer exited") }
    for down in [true, false] {
        let event = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: down)!
        if let text {
            let units = Array(text.utf16)
            units.withUnsafeBufferPointer { event.keyboardSetUnicodeString(stringLength: units.count, unicodeString: $0.baseAddress!) }
        }
        event.postToPid(pid)
    }
    Thread.sleep(forTimeInterval: 0.2)
}
func input() {
    let field = unique { string($0, "AXRole") == "AXTextField" }
    guard AXUIElementSetAttributeValue(field, "AXFocused" as CFString, kCFBooleanTrue) == .success else { fail("cannot focus question editor") }
    key(0, text: args[4])
    guard string(field, "AXValue") == args[4] else { fail("question editor did not receive exact text: " + dump()) }
}
let action = args[3]
if action == "dump" { print(dump()); exit(0) }
guard AXUIElementSetAttributeValue(app, "AXFrontmost" as CFString, kCFBooleanTrue) == .success else {
    fail("cannot activate isolated viewer through Accessibility")
}
Thread.sleep(forTimeInterval: 0.3)
if action == "question-absent" {
    guard !elements().contains(where: { string($0, "AXIdentifier") == "ask-dialog" }) else { fail("stale question in AX tree") }
} else if action.hasPrefix("question-") {
    _ = identified("ask-dialog")
    switch action {
    case "question-input": input(); press("ask-button-confirm")
    case "question-cancel": key(53)
    case "question-buttons": press("ask-button-confirm")
    case "question-default":
        guard string(identified("ask-choice-one"), "AXValue") == "checked" else { fail("first item is not selected") }
        press("ask-button-confirm")
    case "question-choice":
        // Pinned SwiftUI choice rows expose AXPress but do not toggle through it.
        // Use their native keyboard navigation, then verify the exposed selection.
        key(125); key(49)
        guard string(identified("ask-choice-two"), "AXValue") == "checked" else { fail("second choice not selected") }
        press("ask-button-confirm")
    case "question-other":
        input()
        guard string(identified("ask-choice-one"), "AXValue") != "checked" else { fail("Other did not exclude first choice") }
        press("ask-button-confirm")
    case "question-multi":
        key(49); key(125); key(49)
        guard ["one", "two"].allSatisfy({ string(identified("ask-choice-" + $0), "AXValue") == "checked" }) else { fail("multiple choices not selected") }
        press("ask-button-confirm")
    default: fail("unknown question action")
    }
} else if action == "job-input" || action == "job-close" {
    let term = unique { string($0, "AXRole") == "AXTextArea" && (value($0, "AXFocused") as? Bool == true) }
    if action == "job-input" {
        // The actual focused Ghostty AX text surface inserts through its native input path.
        guard AXUIElementSetAttributeValue(term, "AXValue" as CFString, args[4] as CFString) == .success else { fail("terminal AX input failed") }
        key(36)
    } else { key(53) }
} else { fail("unknown AX action") }
print("PASS scoped Accessibility " + action)
