// 用 macOS Vision 的主体抠图，从设定图里把一个人物抠成透明 PNG。只需要做一次。
//   swiftc -O tools/petgen/extract_figure.swift -o /tmp/extract_figure
//   /tmp/extract_figure 设定图.png x y w h 输出.png [保留点x 保留点y]
// (x,y,w,h) 是先裁出来的区域（左上角原点，像素）。Vision 会把区域里的主体分成若干实例，
// 只保留包含「保留点」（相对裁切区域）的那个实例，默认取裁切区域中心。
import AppKit
import CoreImage
import Vision

setvbuf(stdout, nil, _IONBF, 0)
let a = CommandLine.arguments
guard a.count >= 7 else { print("用法: extract_figure 输入 x y w h 输出 [keepX keepY]"); exit(2) }
let crop = CGRect(x: Double(a[2])!, y: Double(a[3])!, width: Double(a[4])!, height: Double(a[5])!)
let keep = a.count >= 9 ? CGPoint(x: Double(a[7])!, y: Double(a[8])!) : CGPoint(x: crop.width / 2, y: crop.height / 2)

guard let src = NSImage(contentsOfFile: a[1])?.cgImage(forProposedRect: nil, context: nil, hints: nil),
      let image = src.cropping(to: crop) else { print("读不了图"); exit(1) }

let request = VNGenerateForegroundInstanceMaskRequest()
try VNImageRequestHandler(cgImage: image).perform([request])
guard let result = request.results?.first else { print("没找到主体"); exit(1) }

// instanceMask 里每个像素是实例编号（0 是背景），先找保留点落在哪个实例上。
let labels = result.instanceMask
CVPixelBufferLockBaseAddress(labels, .readOnly)
let lw = CVPixelBufferGetWidth(labels), lh = CVPixelBufferGetHeight(labels)
let lrow = CVPixelBufferGetBytesPerRow(labels)
let lbase = CVPixelBufferGetBaseAddress(labels)!.assumingMemoryBound(to: UInt8.self)
let kx = Int(keep.x / crop.width * Double(lw)), ky = Int(keep.y / crop.height * Double(lh))
let instance = Int(lbase[ky * lrow + kx])
var counts = [Int: Int]()
for y in 0..<lh { for x in 0..<lw { counts[Int(lbase[y * lrow + x]), default: 0] += 1 } }
CVPixelBufferUnlockBaseAddress(labels, .readOnly)
print("实例像素数(低分辨率):", counts.sorted { $0.key < $1.key }.map { "#\($0.key)=\($0.value)" }.joined(separator: " "))
guard instance != 0 else { print("保留点落在背景上，换一个点"); exit(1) }
print("保留实例 #\(instance)")

let handler = VNImageRequestHandler(cgImage: image)
let mask = try result.generateScaledMaskForImage(forInstances: IndexSet(integer: instance), from: handler)
let alpha = CIImage(cvPixelBuffer: mask)
let input = CIImage(cgImage: image)
let clear = CIImage(color: .clear).cropped(to: input.extent)
let output = input.applyingFilter("CIBlendWithMask", parameters: [kCIInputBackgroundImageKey: clear, kCIInputMaskImageKey: alpha])

let ctx = CIContext()
guard let cg = ctx.createCGImage(output, from: input.extent, format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!) else { exit(1) }
let rep = NSBitmapImageRep(cgImage: cg)
try rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: a[6]))
print("写出 \(a[6]) \(cg.width)x\(cg.height)")
