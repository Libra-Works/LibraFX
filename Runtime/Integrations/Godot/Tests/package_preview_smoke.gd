extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension loaded"):
			return
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "Effect player registered"):
		return
	root.add_child(player)
	player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", BoxMesh.new())
	var preview: Variant = load("res://liberafx_package_preview.gd").new()
	preview.initialize(player)
	var package := FileAccess.get_file_as_bytes("res://adapter.lfxp")
	var identities: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://adapter.ids.json"))
	if not check(identities is Dictionary and identities.has("rootSignalNodeId"),
			"Root Signal identity is available for Editor Send"):
		return
	if not check(preview.consume(frame(1, 1, 0, 0.1, package))["result"] == 0 and preview.tick() == 0,
			"Compiled preview package plays at the editor time"):
		return
	if not check(player.get_node_or_null("LibraFxRootScope") != null, "Preview rendered the root scene"):
		return
	if not check(preview.consume(frame(2, 3, 2, 0.2))["resync"], "Missing delta requests resync"):
		return
	var generation: int = player.get_playback_generation()
	if not check(preview.consume(frame(2, 2, 1, 0.0))["result"] == 0 and preview.tick() == 0
			and player.get_playback_generation() != generation, "Backward seek restarts the native package"):
		return
	var signal_payload := JSON.stringify({"type": "godot_preview_signals", "sequence": "3",
			"timeSeconds": 0.0, "baseSequence": "0",
			"requests": [{"sequence": "1", "nodeId": identities["rootSignalNodeId"]}]}).to_utf8_buffer()
	var send_result: Dictionary = preview.consume(control_frame(signal_payload))
	if not check(send_result["result"] == 0 and preview.tick() == 0,
			"Editor Send packet dispatches the authored root Signal through the Godot package player: %s" %
			preview.error):
		return
	preview.close()
	if not check(not player.is_playing(), "Preview detaches its package"):
		return
	print("PASS: Godot package preview scene, seek, resync and Editor Send")
	quit(0)


func frame(kind: int, sequence: int, baseline: int, time: float,
		payload := PackedByteArray()) -> PackedByteArray:
	var bytes := "LFXP".to_utf8_buffer()
	bytes.resize(48)
	bytes.encode_u16(4, 1)
	bytes.encode_u16(6, kind)
	bytes.encode_u64(8, sequence)
	bytes.encode_u64(16, baseline)
	bytes.encode_double(24, time)
	bytes.encode_double(32, 1.0)
	bytes.encode_u32(44, payload.size())
	bytes.append_array(payload)
	return bytes


func control_frame(payload: PackedByteArray) -> PackedByteArray:
	var bytes := "LFXP".to_utf8_buffer()
	bytes.resize(48)
	bytes.encode_u16(4, 1)
	bytes.encode_u16(6, 4)
	bytes.encode_u32(44, payload.size())
	bytes.append_array(payload)
	return bytes


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
