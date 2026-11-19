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

	var scope: Node = player.get_node_or_null("LibraFxRootScope")
	if not check(scope != null, "Root scope exists"):
		return
	var root_draw: MeshInstance3D = scope.get_node_or_null(str(ids["rootDrawNodeId"]))
	var child_draw: MeshInstance3D = scope.find_child(str(ids["templateDrawNodeId"]), true, false)
	if not check(root_draw != null and child_draw != null, "Both draws exist"):
		return
	var root_material: ShaderMaterial = root_draw.get_surface_override_material(0)
	var child_material: ShaderMaterial = child_draw.get_surface_override_material(0)
	if not check(root_material != null and child_material != null and
			root_material != material and child_material != material and root_material != child_material,
			"Material instances are not shared across scopes or with the bound asset"):
		return
	if not check(is_equal_approx(float(root_material.get_shader_parameter("strength")), 0.75) and
			is_equal_approx(float(material.get_shader_parameter("strength")), 0.125) and
			root_material.get_shader_parameter("effect_color") == Color(0.25, 0.5, 0.75, 1.0) and
			root_material.get_shader_parameter("effect_texture") == texture and
			child_material.get_shader_parameter("effect_texture") == texture,
			"Root defaults and per-draw texture bindings are applied without modifying the asset"):
		return
	var previous_strength := float(child_material.get_shader_parameter("strength"))
	if not check(player.advance(0.125) == 0 and
			float(child_material.get_shader_parameter("strength")) > previous_strength and
			is_equal_approx(float(root_material.get_shader_parameter("strength")), 0.75),
			"Connected child uniform follows child-local Time without modifying the root"):
		return
	print("PASS: Godot applied isolated root/child ShaderMaterial parameters and texture overrides")
	quit(0)


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
