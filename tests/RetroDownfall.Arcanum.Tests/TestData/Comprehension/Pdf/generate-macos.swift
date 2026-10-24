// Real-producer fixtures that only macOS can write, for the Comprehension PDF tests.
//
//   swiftc -O -o "$TMPDIR/macos-fixtures" generate-macos.swift
//   "$TMPDIR/macos-fixtures" webkit <input.html> <output.pdf>   # WebKit (Safari "Export as PDF") through WKWebView.createPDF
//   "$TMPDIR/macos-fixtures" hebrew <output.pdf>                # CoreText into a Quartz PDF context (macOS "Save as PDF")
//
// See README.md for which fixture comes from which command. Neither producer is deterministic (both stamp dates and
// ids), so a regenerated file differs byte for byte; the committed bytes are the reference.
import AppKit
import CoreText
import WebKit

let arguments = CommandLine.arguments

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

class Loader: NSObject, WKNavigationDelegate {
    let output: URL

    init(output: URL) { self.output = output }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        webView.createPDF(configuration: WKPDFConfiguration()) { result in
            switch result {
            case .success(let data):
                try! data.write(to: self.output)
                exit(0)
            case .failure(let error):
                fail("createPDF failed: \(error)")
            }
        }
    }
}

func webkit(html path: String, output: String) -> Never {
    guard let html = try? String(contentsOfFile: path, encoding: .utf8) else { fail("cannot read \(path)") }
    _ = NSApplication.shared
    let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 800, height: 1000))
    let loader = Loader(output: URL(fileURLWithPath: output))
    view.navigationDelegate = loader
    view.loadHTMLString(html, baseURL: nil)
    DispatchQueue.main.asyncAfter(deadline: .now() + 20) { fail("timed out") }
    RunLoop.main.run()
    fail("unreachable")
}

// Two paragraphs. Each takes its direction from its first strong character: the first is right-to-left, so CoreText
// lays it out right-aligned and writes its glyphs in visual order (left to right on the page), which is what the
// extractor has to turn back into logical order.
func hebrew(output: String) {
    var box = CGRect(x: 0, y: 0, width: 612, height: 792)
    guard let context = CGContext(URL(fileURLWithPath: output) as CFURL, mediaBox: &box, nil) else { fail("cannot create \(output)") }
    context.beginPDFPage(nil)
    let text = "\u{05E9}\u{05DC}\u{05D5}\u{05DD} \u{05E2}\u{05D5}\u{05DC}\u{05DD} HEBREW-MARKER\nThe word \u{05E9}\u{05DC}\u{05D5}\u{05DD} means peace."
    let attributed = NSAttributedString(string: text, attributes: [.font: NSFont.systemFont(ofSize: 14)])
    let setter = CTFramesetterCreateWithAttributedString(attributed)
    let path = CGPath(rect: CGRect(x: 72, y: 72, width: 468, height: 648), transform: nil)
    CTFrameDraw(CTFramesetterCreateFrame(setter, CFRange(location: 0, length: 0), path, nil), context)
    context.endPDFPage()
    context.closePDF()
}

switch arguments.count > 1 ? arguments[1] : "" {
case "webkit" where arguments.count == 4:
    webkit(html: arguments[2], output: arguments[3])
case "hebrew" where arguments.count == 3:
    hebrew(output: arguments[2])
default:
    fail("usage: generate-macos webkit <input.html> <output.pdf> | generate-macos hebrew <output.pdf>")
}
