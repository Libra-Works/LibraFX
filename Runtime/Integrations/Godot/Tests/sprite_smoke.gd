extends SceneTree


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	const EXTENSION = "res://liberafx.gdextension"
	const ASSET_IDENTITY = "/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK,
				"Extension library loaded"):
			return

	var package := FileAccess.get_file_as_bytes("res://composition.lfxp")
	if not check(not package.is_empty(), "Compiler-produced sprite composition package exists"):
		return
	var player: Variant = ClassDB.instantiate("LibraEffectPlayer")
	if not check(player != null, "Extension class loaded"):
		return
	root.add_child(player)
	if not check(player.play_package(package) != 0 and not player.is_playing(),
			"Missing Texture2D binding rejects the package"):
		return

	var image := Image.create(64, 32, false, Image.FORMAT_RGBA8)
	image.fill(Color.WHITE)
	var texture := ImageTexture.create_from_image(image)
	if not check(player.set_texture_binding(ASSET_IDENTITY, texture) == 0,
			"Explicit in-memory Texture2D binding is accepted"):
		return
	if not check(player.play_package(package) == 0, "Sprite composition package loads"):
		return
	player.set_process(false)
	if not check(player.advance(0.01) == 0 and player.advance(0.74) == 0,
			"Composition clock advances"):
		return
	if not check(player.set_texture_binding(ASSET_IDENTITY, texture) != 0 and
			player.clear_texture_binding(ASSET_IDENTITY) != 0,
			"Texture bindings cannot change during playback"):
		return

	var scope: Node3D
	for child in player.get_children():
		if child is Node3D and str(child.name).begins_with("LibraFxRootScope"):
			scope = child
			break
	if not check(scope != null, "Root render scope exists"):
		return
	var sprites: Array[MeshInstance3D] = []
	for child in scope.get_children():
		if child is MeshInstance3D:
			sprites.append(child)
	if not check(sprites.size() == 2, "Visible composition tracks create two sprite quads"):
		return

	var back: MeshInstance3D
	var front: MeshInstance3D
	for sprite in sprites:
		if not check(sprite.mesh is QuadMesh, "Sprite uses a quad mesh"):
			return
		var material := (sprite.mesh as QuadMesh).material as StandardMaterial3D
		if not check(material != null and material.albedo_texture == texture and
				material.shading_mode == BaseMaterial3D.SHADING_MODE_UNSHADED and
				material.transparency == BaseMaterial3D.TRANSPARENCY_ALPHA,
				"Bound texture is rendered by an unshaded transparent material"):
			return
		if material.render_priority == 0:
			back = sprite
		elif material.render_priority == 2:
			front = sprite
	if not check(back != null and front != null,
			"Composition draw order is carried to Godot render priority"):
		return

	var backMaterial := (back.mesh as QuadMesh).material as StandardMaterial3D
	var frontMaterial := (front.mesh as QuadMesh).material as StandardMaterial3D
	if not check(backMaterial.uv1_scale.is_equal_approx(Vector3(0.125, 0.75, 1)) and
			backMaterial.uv1_offset.is_equal_approx(Vector3(0.375, 0.25, 0)),
			"Native flipbook sampling selects the second back-track atlas cell"):
		return
	if not check(frontMaterial.uv1_scale.is_equal_approx(Vector3(0.3125, 0.3125, 1)) and
			frontMaterial.uv1_offset.is_equal_approx(Vector3(0.03125, 0.125, 0)),
			"Native flipbook sampling selects the front-track atlas cell"):
		return
	if not check(back.scale.is_equal_approx(Vector3(8, 24, 1)),
			"Composition cell dimensions determine the back-track quad size"):
		return
	if not check(front.position.is_equal_approx(Vector3(6, 0, 0)) and
			is_equal_approx(front.rotation_degrees.z, 50) and
			front.scale.is_equal_approx(Vector3(40, 12.5, 1)),
			"Native composition sampler drives interpolated position, rotation and scale"):
		return

	player.stop()
	if not check(player.clear_texture_binding(ASSET_IDENTITY) == 0,
			"Texture binding can be removed after playback stops"):
		return
	player.free()
	print("PASS: Godot rendered explicit Texture2D sprite composition and sampled native animation")
	quit(0)


func check(condition: bool, label: String) -> bool:
	if not condition:
		push_error(label)
		quit(1)
	return condition
