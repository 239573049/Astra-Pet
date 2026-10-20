// AstraPet 外部验证工具，不参与编译：swiftc -O tools/harness.swift -o /tmp/harness。坐标一律是全局点坐标、左上角原点。
//   harness trusted                 是否有辅助功能权限（CGEventPost 需要）
//   harness bounds                  打印 AstraPet 窗口 x y w h
//   harness underlay x y w h        在给定区域开一个垫底窗口，收到点击就打印
//   harness click x y               在 (x,y) 左键单击，完成后把光标放回原处
//   harness rclick x y              右键单击
//   harness drag x1 y1 x2 y2        按下 → 分步拖到终点 → 松开，完成后光标归位
//   harness move x y                只移动光标
import Cocoa
import ApplicationServices

setvbuf(stdout, nil, _IONBF, 0)
let args = CommandLine.arguments
func num(_ i: Int) -> CGFloat { CGFloat(Double(args[i])!) }

func petBounds() -> CGRect? {
    let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] ?? []
    for w in list where (w[kCGWindowOwnerName as String] as? String) == "AstraPet" {
        if let d = w[kCGWindowBounds as String] as! CFDictionary?, let r = CGRect(dictionaryRepresentation: d) {
            return r
        }
    }
    return nil
}

func post(_ type: CGEventType, _ p: CGPoint, _ button: CGMouseButton = .left) {
    CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: p, mouseButton: button)!
        .post(tap: .cghidEventTap)
    usleep(30_000)
}

func restoring(_ body: () -> Void) {
    let saved = CGEvent(source: nil)!.location
    body()
    usleep(100_000)
    CGWarpMouseCursorPosition(saved)
}

final class LogView: NSView {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with e: NSEvent) { print("UNDERLAY_DOWN") }
    override func rightMouseDown(with e: NSEvent) { print("UNDERLAY_RDOWN") }
    override func draw(_ r: NSRect) { NSColor(calibratedRed: 0.2, green: 0.5, blue: 0.9, alpha: 1).setFill(); r.fill() }
}

switch args[1] {
case "trusted":
    print(AXIsProcessTrusted())
case "bounds":
    if let r = petBounds() { print(Int(r.minX), Int(r.minY), Int(r.width), Int(r.height)) } else { print("none") }
case "underlay":
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)
    let screenH = NSScreen.screens[0].frame.height
    let rect = NSRect(x: num(2), y: screenH - num(3) - num(5), width: num(4), height: num(5))
    let win = NSWindow(contentRect: rect, styleMask: [.borderless], backing: .buffered, defer: false)
    win.contentView = LogView(frame: NSRect(origin: .zero, size: rect.size))
    win.orderFrontRegardless()
    print("UNDERLAY_READY")
    app.run()
case "click":
    restoring { let p = CGPoint(x: num(2), y: num(3)); post(.mouseMoved, p); post(.leftMouseDown, p); post(.leftMouseUp, p) }
case "rclick":
    restoring { let p = CGPoint(x: num(2), y: num(3)); post(.mouseMoved, p); post(.rightMouseDown, p, .right); post(.rightMouseUp, p, .right) }
case "drag":
    restoring {
        let a = CGPoint(x: num(2), y: num(3)), b = CGPoint(x: num(4), y: num(5))
        post(.mouseMoved, a); post(.leftMouseDown, a)
        for i in 1...10 {
            let t = CGFloat(i) / 10
            post(.leftMouseDragged, CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t))
        }
        usleep(150_000)
        post(.leftMouseUp, b)
    }
case "move":
    post(.mouseMoved, CGPoint(x: num(2), y: num(3)))
default:
    print("unknown command")
}
