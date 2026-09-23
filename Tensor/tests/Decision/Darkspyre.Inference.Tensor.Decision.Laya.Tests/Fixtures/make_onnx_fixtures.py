"""Write the tiny ONNX fixtures for LayaOnnxModelTests.

The graphs share the exported Laya DecisionModel's input and output signature but no weights.
They echo their inputs, so a test can check padding, masking and output slicing:

    logits     = float(marker_pos)                        shape [1, K]
    act_logits = [sum(attention_mask), float(qtype)]      shape [1, 2]

Run from this directory with a Python that has `onnx` installed:

    python make_onnx_fixtures.py
"""

import onnx
from onnx import TensorProto, helper

MAX_LEN, MAX_MARKERS = 16, 4


def graph(seq_dim):
    inputs = [
        helper.make_tensor_value_info("input_ids", TensorProto.INT64, [1, seq_dim]),
        helper.make_tensor_value_info("attention_mask", TensorProto.INT64, [1, seq_dim]),
        helper.make_tensor_value_info("marker_pos", TensorProto.INT64, [1, MAX_MARKERS]),
        helper.make_tensor_value_info("marker_mask", TensorProto.BOOL, [1, MAX_MARKERS]),
        helper.make_tensor_value_info("qtype", TensorProto.INT64, [1]),
    ]
    outputs = [
        helper.make_tensor_value_info("logits", TensorProto.FLOAT, [1, MAX_MARKERS]),
        helper.make_tensor_value_info("act_logits", TensorProto.FLOAT, [1, 2]),
    ]
    axes = [helper.make_tensor("axis1", TensorProto.INT64, [1], [1]),
            helper.make_tensor("axis0", TensorProto.INT64, [1], [0])]
    nodes = [
        helper.make_node("Cast", ["marker_pos"], ["logits"], to=TensorProto.FLOAT),
        helper.make_node("Cast", ["attention_mask"], ["mask_f"], to=TensorProto.FLOAT),
        helper.make_node("ReduceSum", ["mask_f", "axis1"], ["mask_sum"], keepdims=1),
        helper.make_node("Cast", ["qtype"], ["qtype_f"], to=TensorProto.FLOAT),
        helper.make_node("Unsqueeze", ["qtype_f", "axis0"], ["qtype_2d"]),
        helper.make_node("Concat", ["mask_sum", "qtype_2d"], ["act_logits"], axis=1),
    ]
    return helper.make_graph(nodes, "laya-fixture", inputs, outputs, initializer=axes)


def save(name, seq_dim, metadata):
    model = helper.make_model(graph(seq_dim), opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 8
    for key, value in metadata.items():
        entry = model.metadata_props.add()
        entry.key, entry.value = key, value
    onnx.checker.check_model(model)
    onnx.save(model, name)


full = {"laya.max_len": str(MAX_LEN), "laya.max_markers": str(MAX_MARKERS),
        "laya.export_version": "1", "laya.source_sha256": "0" * 64}
save("laya-fixture.onnx", MAX_LEN, full)
save("laya-fixture-no-metadata.onnx", MAX_LEN, {})
save("laya-fixture-dynamic.onnx", "seq", full)
