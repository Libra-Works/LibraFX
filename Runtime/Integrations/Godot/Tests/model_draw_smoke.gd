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
	var spawned_scope: Node3D
	for scope in root_scope.get_children():
		if scope is Node3D:
			spawned_scope = scope
			break
	if not check(spawned_scope != null, "Spawned child effect scope exists"):
		return
	if not check(not has_scene_tree_render_nodes(player),
			"Draws use direct RenderingServer instances, not per-draw render Nodes"):
		return
	var render_stats: Dictionary = player.get_render_stats()
	if not check(render_stats.get("render_server_instances", 0) == 2 and
			render_stats.get("effect_scope_nodes", 0) == 2,
			"Root and child draws retain two direct instances and only two scope Nodes"):
		return
	print("OBS: root/child ModelDraw render submission stats = ", render_stats)

	print("PASS: Godot bound root/child ModelDraws to direct RenderingServer instances")
	quit(0)


func has_scene_tree_render_nodes(parent: Node) -> bool:
	for child in parent.get_children():
		if child is MeshInstance3D or child is MultiMeshInstance3D:
			return true
		if has_scene_tree_render_nodes(child):
			return true
	return false


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
