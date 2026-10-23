// Real-producer fixtures that only macOS can write, for the Comprehension PDF tests.
//
//   swiftc -O -o "$TMPDIR/macos-fixtures" generate-macos.swift
//   "$TMPDIR/macos-fixtures" webkit <input.html> <output.pdf>   # WebKit (Safari "Export as PDF") through WKWebView.createPDF
//
// See README.md for which fixture comes from which command. The producer stamps dates and ids, so a regenerated file
// differs byte for byte; the committed bytes are the reference.
import AppKit
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

switch arguments.count > 1 ? arguments[1] : "" {
case "webkit" where arguments.count == 4:
    webkit(html: arguments[2], output: arguments[3])
default:
    fail("usage: macos-fixtures webkit <input.html> <output.pdf>")
}
