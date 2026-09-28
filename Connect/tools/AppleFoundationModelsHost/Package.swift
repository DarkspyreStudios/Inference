// swift-tools-version: 6.0
import PackageDescription

let package = Package(
  name: "AppleFoundationModelsHost",
  platforms: [.macOS("26.0")],
  products: [.executable(name: "darkspyre-apple-fm", targets: ["AppleFoundationModelsHost"])],
  targets: [
    .executableTarget(
      name: "AppleFoundationModelsHost", path: "Sources",
      swiftSettings: [.unsafeFlags(["-warnings-as-errors"])]),
    .testTarget(
      name: "AppleFoundationModelsHostTests", dependencies: ["AppleFoundationModelsHost"],
      path: "Tests", swiftSettings: [.unsafeFlags(["-warnings-as-errors"])]),
  ])
