extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Load extension"):
			return
	var player: Variant = ClassDB.instantiate("LibraFxDebugPlayer")
	if not check(player != null, "Find Godot debug player"):
		return
	root.add_child(player)
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", BoxMesh.new()) == 0,
			"Bind the debugger scene mesh"):
		return
	var invitation := OS.get_environment("LIBERAFX_GODOT_TEST_DEBUG_INVITATION")
	var package := FileAccess.get_file_as_bytes("res://breakpoint.lfxp")
	if not check(not invitation.is_empty() and player.load_package(package) == 0, "Load compiled debug package"):
		return
	var generation: int = player.get_debugger_generation()
	if not check(player.attach_package_debugger(generation + 1, "", 0, invitation) == 13,
			"Reject a stale debugger generation"):
		return
	if not check(player.attach_package_debugger(generation, "", 0, invitation) == 0,
			"Attach the editor's execution debugger"):
		return
	var deadline := Time.get_ticks_msec() + 12000
	while Time.get_ticks_msec() < deadline:
		var submitted: int = player.advance(0.01)
		if not check(submitted == 0, "Submit package work"):
			return
		await create_timer(0.1).timeout
		var status: Dictionary = player.poll()
		while status["busy"] and Time.get_ticks_msec() < deadline:
			var scope := player.get_node_or_null("LibraFxRootScope") as Node3D
			if not check(scope != null and scope.visible and scope.get_child_count() == 1
					and scope.get_child(0) is MeshInstance3D, "Debug scene stays visible while paused"):
				return
			if not FileAccess.file_exists("res://breakpoint-visual.flag"):
				var marker := FileAccess.open("res://breakpoint-visual.flag", FileAccess.WRITE)
				if not check(marker != null, "Report the visible paused scene"):
					return
				marker.close()
			await create_timer(0.02).timeout
			status = player.poll()
		if not check(not status["busy"] and status["result"] == 0, "Resumed package work"):
			return
		if FileAccess.file_exists("res://breakpoint-resumed.flag"):
			player.close()
			quit(0)
			return
	check(false, "Editor never paused the worker")


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
