extends "res://fluid_2d_gpu_smoke.gd"


func run() -> void:
	const EXTENSION := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK, "Extension loads"):
			return
	var owner := Node3D.new()
	root.add_child(owner)
	var player: Variant = load_player(owner)
	player.set_process(false)
	for mode: String in ["generated-fields", "generated-mask"]:
		if not check(player.play_package(FileAccess.get_file_as_bytes("res://" + mode + ".lfxp")) == 0, mode + " package loads: " + player.get_gpu_error()):
			return
		for frame in range(120):
			if not check(player.advance(1.0 / 60.0) == 0, mode + " field consumer advances"):
				return
		var fields: Dictionary = player._test_read_fluid_fields()
		var particles: Dictionary = player._test_read_gpu_particles(0)
		if not check(fields.get("result", -1) == 0 and particles.get("result", -1) == 0, "Test-only field and particle readbacks succeed"):
			return
		var state: PackedFloat32Array = particles.state
		if mode == "generated-fields":
			var expected := Vector2(sample(fields.velocity, .5, .2, 0), sample(fields.velocity, .5, .2, 1)) * 100
			var actual := Vector2(state[4], state[5])
			if not check((actual - expected).length() < .02 and absf(state[6]) < .00001, "Particle samples same-frame raw velocity and applies plane draw scale: " + str([expected, actual])):
				return
		else:
			var density := sample(fields.dye, .5, .2, 3)
			if not check((state[17] != 0) == (density > .01), "Particle Alive matches the raw density threshold mask: " + str(density)):
				return
		player.stop()
		if not check(player.play_package(FileAccess.get_file_as_bytes("res://" + mode + ".lfxp")) == 0 and player.advance(0.0) == 0, "Field consumer restarts cleanly"):
			return
		var restarted: Dictionary = player._test_read_gpu_particles(0)
		if not check(restarted.state[17] == 0, "Restart clears particle storage"):
			return
		player.stop()
		print("PASS same-scope generated field consumer: ", mode)
	quit(0)


func sample(values: PackedFloat32Array, u: float, v: float, lane: int) -> float:
	var x := clampf(u * 24 - .5, 0, 23)
	var y := clampf(v * 40 - .5, 0, 39)
	var x0 := int(floor(x))
	var y0 := int(floor(y))
	var x1 := mini(x0 + 1, 23)
	var y1 := mini(y0 + 1, 39)
	return lerpf(lerpf(values[(y0 * 24 + x0) * 4 + lane], values[(y0 * 24 + x1) * 4 + lane], x - x0), lerpf(values[(y1 * 24 + x0) * 4 + lane], values[(y1 * 24 + x1) * 4 + lane], x - x0), y - y0)
