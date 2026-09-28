// swift-tools-version: 6.2
import PackageDescription

// Only the patched continuation state is needed for these deterministic tests.
// Avoid building Apple's unrelated example and empty static C archive.
let package = Package(
  name: "DSA59LifecycleTests",
  platforms: [.macOS(.v26)],
  targets: [
    .target(name: "FoundationModelsCBindings", path: "Sources"),
    .testTarget(name: "LifecycleTests", dependencies: ["FoundationModelsCBindings"], path: "Tests")
  ]
)
