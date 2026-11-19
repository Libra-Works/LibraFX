extends SceneTree

var _ready_path := ""
var _result_path := ""
var _error_path := ""
var _expected_node_id := ""
var _game_io_routed := false
var _transport: Node
var _player: Node


func _initialize() -> void:
	_run.call_deferred()


func _run() -> void:
	var args := OS.get_cmdline_user_args()
	if not check(args.size() == 4, "Expected certificate, marker paths and Signal UUID"):
		return
	var invitation := OS.get_environment("LIBERAFX_GODOT_TEST_INVITATION")
	var certificate := FileAccess.get_file_as_string(args[0])
	_ready_path = args[1]
	_result_path = args[2]
	_error_path = _result_path + ".error"
	_expected_node_id = args[3].to_lower()
	if not check(not invitation.is_empty() and not certificate.is_empty(),
			"Preview invitation and pinned certificate are available"):
		return

	const EXTENSION := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"LIBERA FX extension loaded"):
			return
	var instance: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(instance != null, "Dedicated LibraEffectPlayer instantiated"):
		return
	_player = instance
	root.add_child(_player)
	if not check(_player.has_signal("game_io_event"),
			"LibraEffectPlayer does not expose game_io_event; child output-signal routing cannot be verified"):
		return
	_player.connect("game_io_event", _on_game_io_event)
	_transport = Node.new()
	_transport.set_script(load("res://liberafx_live_preview.gd"))
	root.add_child(_transport)
	_transport.package_preview_ready.connect(_on_package_ready)
	_transport.preview_signal_dispatched.connect(_on_signal_dispatched)
	_transport.preview_error.connect(_on_preview_error)
	_transport.frame_rejected.connect(_on_frame_rejected)
	_transport.connection_closed.connect(_on_connection_closed)

	var invitation_data: Variant = JSON.parse_string(invitation)
	if not check(invitation_data is Dictionary, "Preview invitation is valid JSON"):
		return
	var wrong_pin: Dictionary = invitation_data.duplicate(true)
	var pin: String = str(wrong_pin["certificateFingerprint"])
	wrong_pin["certificateFingerprint"] = ("0" if pin.substr(0, 1) != "0" else "1") + pin.substr(1)
	var rejected := int(_transport.call("connect_package_local", _player, JSON.stringify(wrong_pin), certificate))
	if not check(rejected == ERR_UNAUTHORIZED, "A mismatched certificate fingerprint is rejected"):
		return

	var connected := int(_transport.call("connect_package_local", _player, invitation, certificate))
	if not check(connected == OK, "Godot WebSocketPeer accepted the pinned local WSS invitation: %d" % connected):
		return
	for _frame in range(1200):
		if FileAccess.file_exists(_error_path):
			_fail(FileAccess.get_file_as_string(_error_path))
			return
		if FileAccess.file_exists(_result_path):
			print("PASS: actual Godot WebSocketPeer received package and dispatched editor Send")
			_transport.call("disconnect_preview")
			quit(0)
			return
		await process_frame
	_fail("Timed out waiting for the real PreviewHost package or Signal dispatch.")


func _on_package_ready(sequence: int) -> void:
	_write_marker(_ready_path, JSON.stringify({"sequence": str(sequence)}))


func _on_signal_dispatched(node_id: String, sequence: int) -> void:
	if node_id.to_lower() != _expected_node_id:
		_fail("Godot dispatched unexpected Signal node %s." % node_id)
		return
	_write_marker(_result_path, JSON.stringify({"nodeId": node_id, "sequence": str(sequence),
			"gameIoRouted": _game_io_routed}))


func _on_preview_error(message: String) -> void:
	_fail("Godot preview error: " + message)


func _on_frame_rejected(result: int) -> void:
	_fail("Godot rejected a PreviewHost frame (result %d)." % result)


func _on_connection_closed() -> void:
	if not FileAccess.file_exists(_result_path):
		_fail("Godot WebSocketPeer closed before Editor Send was dispatched.")


func _on_game_io_event(event: Dictionary) -> void:
	if event.get("kind_name") == "output_signal" and event.get("name") == "godot-send-routed":
		_game_io_routed = true


func _write_marker(path: String, contents: String) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		push_error("Could not write isolated Godot test marker: " + path)
		quit(1)
		return
	file.store_string(contents)
	file.close()


func _fail(message: String) -> void:
	if FileAccess.file_exists(_error_path):
		return
	push_error(message)
	_write_marker(_error_path, message)
	quit(1)


func check(condition: bool, label: String) -> bool:
	if not condition:
		_fail(label)
	return condition
