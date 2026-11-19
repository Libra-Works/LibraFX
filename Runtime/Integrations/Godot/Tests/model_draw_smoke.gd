extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension library loaded"):
			return

	var package := FileAccess.get_file_as_bytes("res://adapter.lfxp")
	var identities: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://adapter.ids.json"))
	if not check(not package.is_empty() and identities is Dictionary,
			"Spawnable ModelDraw fixture and IDs are available"):
		return

	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "Extension class loaded"):
		return
	root.add_child(player)

	var asset_identity := "/Engine/BasicShapes/Cube.Cube"
	if not check(player.play_package(package) != 0 and not player.is_playing(),
			"Missing Mesh binding is rejected explicitly"):
		return
	var mesh := BoxMesh.new()
	if not check(player.set_mesh_binding(asset_identity, mesh) == 0,
			"Explicit in-memory Mesh binding is accepted"):
		return
	if not check(player.play_package(package) == 0, "Spawnable ModelDraw package loads"):
		return
	player.set_process(false)
	if not check(player.advance(0.01) == 0, "Root package starts"):
		return
	if not check(player.dispatch_signal(str(identities["rootSignalNodeId"])) == 0,
			"Fixture signal spawns the child scope"):
		return
	if not check(player.advance(0.25) == 0, "Root and spawned scopes advance"):
		return

	var root_scope: Node3D
	for child in player.get_children():
		if child is Node3D and str(child.name).begins_with("LibraFxRootScope"):
			root_scope = child
			break
	if not check(root_scope != null, "Root render scope exists"):
		return
	var root_draw: MeshInstance3D = root_scope.get_node_or_null(str(identities["rootDrawNodeId"]))
	if not check(root_draw != null and root_draw.mesh == mesh,
			"Root draw uses the explicitly bound Mesh"):
		return
	if not check(root_draw.position == Vector3.ZERO and
			root_draw.rotation_degrees == Vector3.ZERO and root_draw.scale == Vector3.ONE,
			"Root position, rotation and scale defaults are applied"):
		return

	var template_draw: MeshInstance3D
	for scope in root_scope.get_children():
		if scope is MeshInstance3D:
			continue
		for draw in scope.get_children():
			if draw is MeshInstance3D and draw.name == str(identities["templateDrawNodeId"]):
				template_draw = draw
				break
	if not check(template_draw != null and template_draw.mesh == mesh,
			"Spawned child draw uses its own explicit Mesh scope"):
		return
	if not check(template_draw.position.x > 0.0 and
			is_equal_approx(template_draw.position.x, template_draw.position.y) and
			is_equal_approx(template_draw.position.y, template_draw.position.z),
			"Child position follows the fixture's runtime Time input"):
		return
	if not check(template_draw.rotation_degrees == Vector3.ZERO and template_draw.scale == Vector3.ONE,
			"Child rotation and scale defaults are applied"):
		return

	print("PASS: Godot bound and advanced root/child ModelDraw scopes")
	quit(0)


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
