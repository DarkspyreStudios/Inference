import Foundation
import FoundationModels

enum Schemas {
  static func build(_ plan: SchemaNode) throws -> GenerationSchema {
    var counter = 0
    return try GenerationSchema(
      root: buildNode(plan, counter: &counter, depth: 0), dependencies: [])
  }

  private static func buildNode(_ node: SchemaNode, counter: inout Int, depth: Int) throws
    -> DynamicGenerationSchema
  {
    guard depth <= 64 else { throw HostFailure(code: "invalid_request") }
    counter += 1
    let name = "Node_\(counter)"
    switch node.kind {
    case "object":
      guard let properties = node.properties, Set(properties.map(\.name)).count == properties.count
      else { throw HostFailure(code: "invalid_request") }
      var converted: [DynamicGenerationSchema.Property] = []
      for property in properties {
        converted.append(
          .init(
            name: property.name, description: property.schema.description,
            schema: try buildNode(property.schema, counter: &counter, depth: depth + 1),
            isOptional: !property.required))
      }
      return .init(name: name, description: node.description, properties: converted)
    case "array":
      guard let items = node.items else { throw HostFailure(code: "invalid_request") }
      guard node.minItems.map({ $0 >= 0 }) ?? true, node.maxItems.map({ $0 >= 0 }) ?? true,
        (node.minItems ?? 0) <= (node.maxItems ?? Int.max)
      else { throw HostFailure(code: "invalid_request") }
      return .init(
        arrayOf: try buildNode(items, counter: &counter, depth: depth + 1),
        minimumElements: node.minItems, maximumElements: node.maxItems)
    case "string":
      if let choices = node.choices, choices.isEmpty || Set(choices).count != choices.count {
        throw HostFailure(code: "invalid_request")
      }
      return .init(type: String.self, guides: node.choices.map { [.anyOf($0)] } ?? [])
    case "boolean": return .init(type: Bool.self)
    case "integer":
      if let low = node.minimum.flatMap(Int.init), let high = node.maximum.flatMap(Int.init),
        low > high
      {
        throw HostFailure(code: "invalid_request")
      }
      var guides: [GenerationGuide<Int>] = []
      if let value = node.minimum {
        guard let bound = Int(value) else { throw HostFailure(code: "invalid_request") }
        guides.append(.minimum(bound))
      }
      if let value = node.maximum {
        guard let bound = Int(value) else { throw HostFailure(code: "invalid_request") }
        guides.append(.maximum(bound))
      }
      return .init(type: Int.self, guides: guides)
    case "number":
      if let low = node.minimum.flatMap(Double.init), let high = node.maximum.flatMap(Double.init),
        low > high
      {
        throw HostFailure(code: "invalid_request")
      }
      var guides: [GenerationGuide<Double>] = []
      if let value = node.minimum {
        guard let bound = Double(value), bound.isFinite else {
          throw HostFailure(code: "invalid_request")
        }
        guides.append(.minimum(bound))
      }
      if let value = node.maximum {
        guard let bound = Double(value), bound.isFinite else {
          throw HostFailure(code: "invalid_request")
        }
        guides.append(.maximum(bound))
      }
      return .init(type: Double.self, guides: guides)
    default: throw HostFailure(code: "unsupported_feature")
    }
  }
}
