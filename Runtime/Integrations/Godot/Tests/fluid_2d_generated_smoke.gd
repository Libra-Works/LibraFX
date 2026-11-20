extends "res://fluid_2d_gpu_smoke.gd"


func run() -> void:
	const EXTENSION := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK, "Extension loads"):
			return
	var viewport := make_viewport()
	var player: Variant = load_player(viewport)
	var package := FileAccess.get_file_as_bytes("res://generated-output.lfxb")
	if not check(player.play_package(package) == 0, "Generated resource package loads: " + player.get_gpu_error()):
		return
	player.set_process(false)
	for step in range(30):
		if not check(player.advance(1.0 / 60.0) == 0, "Generated resource frame advances: " + player.get_gpu_error()):
			return
	var generated: Dictionary = await capture(viewport)
	if not check(generated.colored > 10 and generated.translucent > 0, "GPU consumer draws generated dye: " + str(generated)):
		return
	var particles: Dictionary = player._test_read_gpu_particles(0)
	if not check(particles.get("result", -1) == 0, "Generated consumer has GPU particle storage"):
		return
	var peer: Variant = load_player(viewport)
	if not check(peer.play_package(package) == 0, "Independent generated resource scope loads"):
		return
	peer.set_process(false)
	player.stop()
	var blank: Dictionary = await capture(viewport)
	if not check(blank.colored == 0, "Retirement clears the consumer; sibling source remains initially blank"):
		return
	for step in range(30):
		if not check(peer.advance(1.0 / 60.0) == 0, "Sibling consumer survives producer retirement"):
			return
	var surviving: Dictionary = await capture(viewport)
	if not check(surviving.colored == generated.colored, "Independent scope has the same deterministic output"):
		return
	peer.stop()
	if not check(player.play_package(package) == 0 and player.advance(0.0) == 0, "Generated resource scope restarts"):
		return
	player.set_process(false)
	var restarted: Dictionary = await capture(viewport)
	if not check(restarted.colored == 0, "Restart clears generated storage"):
		return
	player.stop()
	if not check(player.play_package(FileAccess.get_file_as_bytes("res://fluid-only.lfxb")) == 0, "Automatic plane control loads"):
		return
	player.set_process(false)
	for step in range(30):
		if not check(player.advance(1.0 / 60.0) == 0, "Control advances"):
			return
	var control: Dictionary = await capture(viewport)
	# The existing particle quad spans [-1,1]; the automatic plane spans [-0.5,0.5].
	if not check(control.colored > 10 and absf((generated.centroid_y - HEIGHT * 0.5) * 0.5 -
			(control.centroid_y - HEIGHT * 0.5)) < 5.0,
			"Generated texture preserves the source vertical orientation: " + str(control)):
		return
	print("PASS generated GPU texture: consumer=", generated, "; source plane=", control, "; independent retirement and fresh restart")
	player.stop()
	quit(0)
