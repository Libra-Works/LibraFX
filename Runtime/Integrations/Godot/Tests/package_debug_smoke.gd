extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension loaded"):
			return
	var player: Variant = ClassDB.instantiate("LibraFxDebugPlayer")
	if not check(player != null, "Debug player registered"):
		return
	root.add_child(player)
	# The adapter fixture contains ModelDraw in both root and child scopes.
	# Debug playback now renders those scopes, so bind the authored asset.
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", BoxMesh.new()) == 0,
			"Debug player accepts an explicit mesh binding"):
		return
	var package := FileAccess.get_file_as_bytes("res://adapter.lfxp")
	var ids: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://adapter.ids.json"))
	if not check(ids is Dictionary and player.load_package(package) == 0
			and player.get_debugger_generation() > 0, "Debug player loaded compiled package"):
		return
	if not check(player.advance(0.0) == 0, "Debug player submitted initial evaluation"):
		return
	var status := await completed(player)
	if not check(not status["busy"] and status["result"] == 0, "Initial evaluation completed: %s" % status):
		return
	if not check(player.dispatch_signal(ids["rootSignalNodeId"]) == 0, "Debug player submitted a signal"):
		return
	status = await completed(player)
	if not check(not status["busy"] and status["result"] == 0, "Signal job completed: %s" % status):
		return
	var advance_result: int = player.advance(0.25)
	if not check(advance_result == 0, "Debug player submitted package step: %d" % advance_result):
		return
	status = await completed(player)
	if not check(not status["busy"] and status["result"] == 0, "Package worker completed"):
		return
	var event: Dictionary
	for _event in range(128):
		event = player.next_package_event()
		if not event["has_event"] or event["kind"] == 1:
			break
	if not check(event["has_event"] and event["container_id"] == ids["spawnableContainerId"],
			"Worker spawned the authored child"):
		return
	var value: Dictionary = player.read_package_value(ids["timeNodeId"], event["container_id"], event["instance_id"])
	if not check(value["result"] == 0 and value["values"].size() == 1, "Read typed child value after idle"):
		return
	player.close()
	for _frame in range(120):
		if player.poll().get("closed", false):
			print("PASS: Godot package debug worker, child scope and close")
			quit(0)
			return
		await process_frame
	check(false, "Package debug player closed")


func completed(player: Node) -> Dictionary:
	var status: Dictionary
	for _frame in range(120):
		status = player.poll()
		if not status["busy"]:
			break
		await process_frame
	return status


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
