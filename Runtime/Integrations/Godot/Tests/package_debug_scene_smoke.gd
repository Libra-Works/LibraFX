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
	root.add_child(player)
	var package := FileAccess.get_file_as_bytes("res://adapter.lfxp")
	var ids: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://adapter.ids.json"))
	if not check(ids is Dictionary and not package.is_empty(), "ModelDraw fixture exists"):
		return
	if not check(player.load_package(package) != 0 and player.get_child_count() == 0,
			"Missing mesh binding fails without leaving a partial scene"):
		return
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = Color(0.9, 0.1, 0.1)
	var mesh := BoxMesh.new()
	mesh.material = material
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", mesh) == 0
			and player.load_package(package) == 0, "Explicit debug mesh binding loads"):
		return
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", mesh) != 0,
			"Bindings cannot mutate an active debug session"):
		return
	if not check(player.advance(0.0) == 0, "Initial worker submitted"):
		return
	var status := await completed(player)
	if not check(status["result"] == 0 and status["visual_preview"]
			and status["render_result"] == 0, "Initial worker publishes the scene: %s" % status):
		return
	var root_scope := player.get_node_or_null("LibraFxRootScope") as Node3D
	var root_draw: MeshInstance3D
	if root_scope != null:
		root_draw = root_scope.get_node_or_null(str(ids["rootDrawNodeId"])) as MeshInstance3D
	if not check(root_draw != null and root_draw.mesh == mesh
			and root_draw.position == Vector3.ZERO and root_draw.scale == Vector3.ONE,
			"Root debug draw uses authored transform and explicitly bound mesh"):
		return
	if not check(player.dispatch_signal(str(ids["rootSignalNodeId"])) == 0, "Spawn signal submitted"):
		return
	status = await completed(player)
	if not check(status["result"] == 0, "Spawn worker publishes child scene: %s" % status):
		return
	var spawn := find_event(player, 1)
	if not check(spawn.get("has_event", false)
			and spawn["container_id"] == ids["spawnableContainerId"],
			"Rendering preserves the child-selection Spawn event"):
		return
	var child_path := "%s_%d/%s" % [spawn["container_id"], spawn["instance_id"], ids["templateDrawNodeId"]]
	var child_draw := root_scope.get_node_or_null(child_path) as MeshInstance3D
	if not check(child_draw != null and child_draw.mesh == mesh, "Spawn created a separate child draw"):
		return
	var last_transform := child_draw.transform
	if not check(player.advance(0.25) == 0 and child_draw.transform == last_transform,
			"Submitting work does not update the scene before completion is acquired"):
		return
	var busy_value: Dictionary = player.read_package_value(ids["timeNodeId"],
		spawn["container_id"], spawn["instance_id"])
	if not check(busy_value["result"] != 0, "Unacquired worker values are inaccessible"):
		return
	status = await completed(player)
	if not check(status["result"] == 0 and child_draw.position.x > 0.0
			and is_equal_approx(child_draw.position.x, child_draw.position.y)
			and is_equal_approx(child_draw.position.y, child_draw.position.z),
			"Idle completion applies the child runtime Time-driven position"):
		return
	last_transform = child_draw.transform
	for _poll in range(4):
		player.poll()
	if not check(child_draw.transform == last_transform, "Idle polling does not advance the last frame"):
		return
	if OS.get_cmdline_user_args().has("--pixels"):
		var camera := Camera3D.new()
		camera.projection = Camera3D.PROJECTION_ORTHOGONAL
		camera.size = 4.0
		camera.position = Vector3(0, 0, 6)
		camera.environment = Environment.new()
		camera.environment.background_mode = Environment.BG_COLOR
		camera.environment.background_color = Color.BLACK
		root.add_child(camera)
		camera.make_current()
		await RenderingServer.frame_post_draw
		await RenderingServer.frame_post_draw
		var pixels := root.get_texture().get_image()
		var center := pixels.get_pixel(pixels.get_width() / 2, pixels.get_height() / 2)
		if not check(center.r > 0.5 and center.r > center.g * 2.0,
				"Completed CPU debug frame produced red center pixels: %s" % center):
			return
	if not check(player.force_terminate(spawn["container_id"], spawn["instance_id"]) == 0
			and root_scope.get_node_or_null(child_path) == null,
			"Idle force termination removes the child scene immediately"):
		return
	var retired := find_event(player, 5)
	if not check(retired.get("has_event", false)
			and retired["instance_id"] == spawn["instance_id"],
			"Retirement remains visible to the debugger event consumer"):
		return
	player.close()
	status = player.poll()
	if not check(status.get("closed", false) and player.get_child_count() == 0,
			"Closing an idle debug player removes all owned scene scopes"):
		return
	print("PASS: Godot debug root/child scene, acquire boundary, event preservation and cleanup")
	quit(0)


func find_event(player: Node, kind: int) -> Dictionary:
	for _index in range(128):
		var event: Dictionary = player.next_package_event()
		if not event.get("has_event", false) or event["kind"] == kind:
			return event
	return {}


func completed(player: Node) -> Dictionary:
	var deadline := Time.get_ticks_msec() + 10000
	var status: Dictionary = player.poll()
	while status["busy"] and Time.get_ticks_msec() < deadline:
		await process_frame
		status = player.poll()
	return status


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
