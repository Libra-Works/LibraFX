extends SceneTree

const CLOCK = "416910d7-f3d0-4d52-a4d3-359e12c23b41"
const POSITION = "d316826d-e06c-454d-a652-b3a36f0188a6"


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
	root.add_child(player)
	var program := FileAccess.get_file_as_bytes("res://clock.lfxb")
	if not check(player.play(program, 1.0) == 0, "Converter output starts"):
		return
	player.set_process(false)
	if not check(player.advance(0.25) == 0, "Playback steps"):
		return
	if not check(player.read_value(CLOCK) == PackedFloat64Array([0.25]), "Scalar time output"):
		return
	if not check(player.read_value(POSITION) == PackedFloat64Array([1, 2, 3]), "Typed vector output"):
		return
	if not check(player.read_value("bad-uuid").is_empty(), "Invalid node identity"):
		return
	if not check(player.play(PackedByteArray([0, 0])) != 0
			and player.read_value(CLOCK) == PackedFloat64Array([0.25]),
			"Invalid replacement keeps the previous playback"):
		return
	if not check(player.advance(0.75) == 0 and not player.is_playing(), "Finite lifetime ends"):
		return
	if not check(player.play(program) == 0, "Restart after completion"):
		return
	await process_frame
	await process_frame
	var frame_clock: PackedFloat64Array = player.read_value(CLOCK)
	if not check(frame_clock.size() == 1 and frame_clock[0] > 0, "Godot advances playback automatically"):
		return
	root.remove_child(player)
	if not check(not player.is_playing(), "Exiting the scene releases playback"):
		return
	player.free()
	print("PASS: Godot GDExtension executed converter-produced LIBERA FX bytecode")
	quit(0)


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
