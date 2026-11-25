extends SceneTree

var received_events: Array[Dictionary] = []
var active_player: Variant
var replace_on_event := false
var replacement_result := -1
var replacement_bytecode := PackedByteArray()


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension library loaded"):
			return

	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "Extension class loaded"):
		return
	active_player = player
	var camera := Camera3D.new()
	camera.position = Vector3(10.0, 20.0, 30.0)
	root.add_child(camera)
	root.add_child(player)
	player.camera_path = NodePath("../Camera3D")
	player.game_io_event.connect(_on_game_io_event)

	var package := FileAccess.get_file_as_bytes("res://spawnable.lfxp")
	var package_result: int = player.play_package(package)
	if not check(package_result == 0, "CPU Spawnable package loads (result %d)" % package_result):
		return
	if not check(player.play(package) == 0,
			"Unified play accepts compiled .lfxp packages"):
		return
	var generation: int = player.get_playback_generation()
	if not check(player.play(package.slice(0, 24)) != 0
			and player.get_playback_generation() == generation and player.is_playing(),
			"Truncated LFXP is rejected without replacing the active package"):
		return
	player.set_process(false)

	var ports: Array = player.get_game_io_ports()
	var property_name := ""
	var signal_name := ""
	for port in ports:
		if port["kind_name"] == "input_property" and property_name.is_empty():
			property_name = port["name"]
		elif port["kind_name"] == "input_signal" and signal_name.is_empty():
			signal_name = port["name"]
	if not check(not property_name.is_empty() and not signal_name.is_empty(),
			"Named property and signal inputs are exposed"):
		return

	var property_port: Dictionary = {}
	for port in ports:
		if port["name"] == property_name:
			property_port = port
			break
	var input_values := PackedFloat64Array()
	input_values.resize(property_port["width"])
	for index in range(input_values.size()):
		input_values[index] = 0.0
	if not check(player.set_game_property(property_name, input_values) == 0,
			"Named property input is accepted"):
		return
	if not check(player.advance(0.01) == 0, "Package starts with initial inputs"):
		return
	received_events.clear()
	replacement_bytecode = FileAccess.get_file_as_bytes("res://clock.lfxp")
	if not check(not replacement_bytecode.is_empty(),
			"A second compiled package is available for the reentrant replacement check"):
		return
	replace_on_event = true
	if not check(player.fire_game_signal(signal_name) == 0,
			"Named signal input is accepted"):
		return
	if not check(player.advance(0.01) == 0, "Signal-driven package advances"):
		return
	if not check(replacement_result == 0 and player.is_playing(),
			"Game I/O callback can replace package playback"):
		return
	player.set_process(false)
	if not check(has_event("", "output_signal", []),
			"Named output signal event is emitted"):
		return
	if not verify_script_action(player):
		return

	var camera_package := FileAccess.get_file_as_bytes("res://CurrentCamera.lfxp")
	if not check(not camera_package.is_empty() and player.play_package(camera_package) == 0,
			"Compiler-produced CurrentCamera package loads"):
		return
	player.set_process(false)
	received_events.clear()
	if not check(player.advance(0.01) == 0, "Explicit Camera3D snapshot advances"):
		return
	if not check(has_event("cameraPosition", "output_property", [10.0, 20.0, 30.0]) and
			has_event("cameraValid", "output_property", [1.0]),
			"CurrentCamera outputs position and validity on separate named ports"):
		return

	camera.position = Vector3(40.0, 50.0, 60.0)
	camera.current = true
	player.camera_path = NodePath("../DeletedCamera")
	if not check(player.advance(0.01) == 0, "Invalid override falls back to viewport camera"):
		return
	if not check(has_event("cameraPosition", "output_property", [40.0, 50.0, 60.0]) and
			has_event("cameraValid", "output_property", [1.0]),
			"Viewport camera is used when the explicit path is invalid"):
		return

	camera.current = false
	received_events.clear()
	if not check(player.advance(0.01) == 0, "Package advances without any camera"):
		return
	if not check(has_event("cameraPosition", "output_property", [0.0, 0.0, 0.0]) and
			has_event("cameraValid", "output_property", [0.0]),
			"Missing camera produces zero position and invalid flag"):
		return

	if not check(player.play_package(PackedByteArray([0, 0])) != 0 and player.is_playing(),
			"Invalid package preserves current playback"):
		return
	var unsupported_package := FileAccess.get_file_as_bytes("res://unsupported_gpu.lfxp")
	if not check(not unsupported_package.is_empty(), "Unsupported-payload fixture is available"):
		return
	var unsupported_result: int = player.play_package(unsupported_package)
	if not check(unsupported_result in [3, 7] and player.is_playing(),
			"Render/GPU payload is explicitly rejected without replacing playback"):
		return
	print("PASS: Godot played CPU .lfxp and routed named Game I/O")
	quit(0)


func verify_script_action(player: Variant) -> bool:
	var package := FileAccess.get_file_as_bytes("res://ScriptAction.lfxp")
	var calls: Array[String] = []
	var handler := func(event: Dictionary) -> PackedFloat64Array:
		if OS.get_thread_caller_id() == OS.get_main_thread_id():
			calls.append(event["name"])
		return PackedFloat64Array([42.0])
	if not check(player.bind_script_action("action.sample", handler, "reply")
			and not player.bind_script_action("action.sample", handler, "reply"),
			"Action binds before playback and refuses duplicate names"):
		return false
	if not check(player.play_package(package) == 0, "Script action fixture loads"):
		return false
	player.set_process(false)
	if not check(player.advance(0.01) == 0, "Script action fixture starts"):
		return false
	received_events.clear()
	if not check(player.fire_game_signal("trigger") == 0 and player.advance(0.01) == 0
			and calls == ["action.sample"] and has_event("action.sample", "output_signal", [])
			and not has_event("observed", "output_property", [42.0]),
			"Action runs on the main thread and still reaches ordinary listeners"):
		return false
	if not check(player.advance(0.01) == 0 and has_event("observed", "output_property", [42.0]),
			"Action result reaches the graph on the next evaluation"):
		return false
	calls.clear()
	var replace_handler := func(event: Dictionary) -> PackedFloat64Array:
		if player.play_package(package) == 0:
			calls.append(event["name"])
		return PackedFloat64Array([999.0])
	if not check(player.unbind_script_action("action.sample")
			and player.bind_script_action("action.sample", replace_handler, "reply")
			and player.fire_game_signal("trigger") == 0 and player.advance(0.01) == 0
			and calls == ["action.sample"], "Action can replace its playback"):
		return false
	player.set_process(false)
	received_events.clear()
	return check(player.advance(0.01) == 0 and has_event("observed", "output_property", [0.0])
		and not has_event("observed", "output_property", [999.0]),
		"Action result never leaks into replacement playback")


func _on_game_io_event(event: Dictionary) -> void:
	received_events.append(event)
	if replace_on_event and event["kind_name"] == "output_signal":
		replace_on_event = false
		replacement_result = active_player.play(replacement_bytecode)


func has_event(port_name: String, kind_name: String, expected_values: Array) -> bool:
	for event in received_events:
		if event["kind_name"] != kind_name:
			continue
		if not port_name.is_empty() and event["name"] != port_name:
			continue
		var values: PackedFloat64Array = event["values"]
		if values.size() != expected_values.size():
			continue
		var matches := true
		for index in range(values.size()):
			if not is_equal_approx(values[index], expected_values[index]):
				matches = false
				break
		if matches:
			return true
	return false


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
