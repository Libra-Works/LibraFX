extends "res://fluid_2d_gpu_smoke.gd"


func run() -> void:
	const EXTENSION := "res://liberafx.gdextension"
	if not GDExtensionManager.is_extension_loaded(EXTENSION):
		if not check(GDExtensionManager.load_extension(EXTENSION) == GDExtensionManager.LOAD_STATUS_OK, "Extension loads"):
			return
	var viewport := make_viewport()
	var first: Variant = load_player(viewport)
	var second: Variant = load_player(viewport)
	first.set_process(false)
	second.set_process(false)
	for mode: String in ["generated-sprite", "generated-flexible", "generated-material", "generated-material-dye", "generated-material-fields"]:
		if mode.begins_with("generated-material"):
			var shader := Shader.new()
			var decode := "value.rgb / max(value.a, .000001)" if mode.ends_with("-dye") else "value.rgb"
			var alpha := "value.a"
			if mode.ends_with("-fields"):
				decode = "vec3(value.b)"
				alpha = "clamp(value.b, 0., 1.)"
			var coordinates := "UV" if mode == "generated-material" else "vec2(UV.x, 1.0 - UV.y)"
			shader.code = "shader_type spatial; render_mode unshaded, cull_disabled; uniform sampler2D FluidTexture; void fragment() { vec4 value = texture(FluidTexture, " + coordinates + "); ALBEDO = " + decode + "; ALPHA = " + alpha + "; }"
			var material := ShaderMaterial.new()
			material.shader = shader
			var mesh := QuadMesh.new()
			mesh.size = Vector2(100, 100)
			for consumer in [first, second]:
				if not check(consumer.set_mesh_binding("/Game/LibraTests/GeneratedMesh.GeneratedMesh", mesh) == 0 and consumer.set_material_binding("/Game/LibraTests/GeneratedMaterial.GeneratedMaterial", material) == 0, "Explicit project material and mesh bindings"):
					return
		var package := FileAccess.get_file_as_bytes("res://" + mode + ".lfxp")
		if not check(first.play_package(package) == 0, mode + " compiled package loads: " + first.get_gpu_error()):
			return
		first.set_process(false)
		for frame in range(30):
			if not check(first.advance(1.0 / 60.0) == 0, mode + " advances"):
				return
		var rendered: Dictionary = await capture(viewport, true)
		if not check(rendered.colored > 10 and rendered.translucent > 0, mode + " ordinary draw samples generated color: " + str(rendered)):
			return
		save_capture(viewport, "fluid-" + mode + ".png")
		if not check(second.play_package(package) == 0, "Independent draw consumer scope loads"):
			return
		second.set_process(false)
		first.stop()
		var blank: Dictionary = await capture(viewport, true)
		if not check(blank.colored == 0, "Producer retirement invalidates the ordinary consumer; new sibling is still clear"):
			return
		for frame in range(30):
			if not check(second.advance(1.0 / 60.0) == 0, "Independent scope survives other producer retirement"):
				return
		var replay: Dictionary = await capture(viewport, true)
		if not check(replay.colored == rendered.colored, "Independent ordinary draw replays the same GPU state"):
			return
		second.stop()
		print("PASS ", mode, ": ", rendered, "; independent producer lifetime and replay")
	quit(0)
