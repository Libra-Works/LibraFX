extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension library loaded"):
			return
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "Player class loaded"):
		return
	root.add_child(player)
	var package := FileAccess.get_file_as_bytes("res://material.lfxp")
	var ids: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://material.ids.json"))
	if not check(not package.is_empty() and ids is Dictionary, "Material fixture exists"):
		return
	var mesh := BoxMesh.new()
	var material := ShaderMaterial.new()
	material.shader = Shader.new()
	material.shader.code = """
shader_type spatial;
uniform float strength = 0.125;
uniform vec4 effect_color : source_color;
uniform sampler2D effect_texture : source_color;
void fragment() {
	ALBEDO = effect_color.rgb * strength * texture(effect_texture, UV).rgb;
}
"""
	material.set_shader_parameter("strength", 0.125)
	var image := Image.create_empty(2, 2, false, Image.FORMAT_RGBA8)
	image.fill(Color.WHITE)
	var texture := ImageTexture.create_from_image(image)
	if not check(player.set_mesh_binding("/Engine/BasicShapes/Cube.Cube", mesh) == 0 and
			player.play_package(package) != 0 and not player.is_playing(),
			"Missing explicitly bound material is rejected"):
		return
	if not check(player.set_material_binding("/Game/FX/ModelMaterial", material) == 0 and
			player.play_package(package) != 0 and not player.is_playing(),
			"Missing explicitly bound texture is rejected"):
		return
	var wrong_material := ShaderMaterial.new()
	wrong_material.shader = Shader.new()
	wrong_material.shader.code = "shader_type spatial; uniform vec4 wrong_name;"
	if not check(player.set_texture_binding("/Game/FX/MaterialTexture", texture) == 0 and
			player.set_material_binding("/Game/FX/ModelMaterial", wrong_material) == 0 and
			player.play_package(package) != 0 and not player.is_playing(),
			"Missing shader uniform is rejected"):
		return
	if not check(player.set_material_binding("/Game/FX/ModelMaterial", material) == 0 and
			player.play_package(package) == 0, "Complete material bindings play"):
		return
	player.set_process(false)
	if not check(player.advance(0.0) == 0 and
			player.dispatch_signal(str(ids["rootSignalNodeId"])) == 0 and
			player.advance(0.125) == 0, "Root and child advance"):
		return

	if not check(not has_scene_tree_render_nodes(player),
			"Material draws use direct RenderingServer instances"):
		return
	var render_stats: Dictionary = player.get_render_stats()
	if not check(render_stats.get("render_server_instances", 0) == 2 and
			render_stats.get("effect_scope_nodes", 0) == 2 and
			material.get_shader_parameter("strength") == 0.125,
			"Root and child material draws stay isolated without per-draw Nodes or changing the bound material"):
		return
	if not check(player.advance(0.125) == 0,
			"Connected child material parameters continue updating with the child scope"):
		return
	print("PASS: Godot admitted isolated root/child ShaderMaterial draws as direct RenderingServer instances")
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
