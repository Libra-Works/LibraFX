extends SceneTree

# Test-only direct dispatch of the exact shader embedded by PackageGpuRenderer.
# Inputs, packets and expected values come from the current Core test exporter.
var device: RenderingDevice
var shader: RID
var pipeline: RID
var failures := 0
var comparisons := 0


func _initialize() -> void:
	run.call_deferred()


func run() -> void:
	print("Godot %s; adapter: %s; driver: %s" % [
		Engine.get_version_info().string, RenderingServer.get_video_adapter_name(),
		RenderingServer.get_video_adapter_api_version()])
	device = RenderingServer.create_local_rendering_device()
	if device == null:
		fail("A Vulkan local RenderingDevice is required")
		quit(1)
		return
	var source_text := FileAccess.get_file_as_string("res://LgpkCompute.glsl.inc")
	if not source_text.begins_with("R\"LGPK(") or not source_text.strip_edges().ends_with(")LGPK\""):
		fail("Unexpected embedded shader format")
		quit(1)
		return
	source_text = source_text.replace("R\"LGPK(", "").replace(")LGPK\"", "")
	var source := RDShaderSource.new()
	source.source_compute = source_text
	var spirv := device.shader_compile_spirv_from_source(source)
	if spirv == null or not spirv.compile_error_compute.is_empty():
		fail("Compute shader compilation failed: " + (spirv.compile_error_compute if spirv != null else "no SPIR-V"))
		quit(1)
		return
	shader = device.shader_create_from_spirv(spirv)
	pipeline = device.compute_pipeline_create(shader)
	if not shader.is_valid() or not pipeline.is_valid():
		fail("Could not create compute shader/pipeline")
		quit(1)
		return
	var math: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://math.reference.json"))
	var noise: Variant = JSON.parse_string(FileAccess.get_file_as_string("res://noise.reference.json"))
	if not (math is Dictionary and noise is Dictionary):
		fail("Missing or invalid Core reference files")
	else:
		run_math(math)
		run_noise(noise)
	device.free_rid(pipeline)
	device.free_rid(shader)
	device.free()
	print("Godot Vulkan canonical result: %d scalar comparisons, %d failures" % [comparisons, failures])
	quit(0 if failures == 0 else 1)


func run_math(reference: Dictionary) -> void:
	var cases: Array = reference.cases
	var groups := {}
	for item in cases:
		var id: String = item.kernelId
		if not groups.has(id):
			groups[id] = []
		groups[id].append(item)
	if cases.size() != 54 or groups.size() != 18:
		fail("Unexpected canonical math coverage: %d cases / %d kernels" % [cases.size(), groups.size()])
		return
	for id in groups:
		var items: Array = groups[id]
		var output := dispatch("math." + id + ".lgpk", items)
		if output.is_empty():
			continue
		for i in items.size():
			var item: Dictionary = items[i]
			var offset: int = {"Position": 0, "Color": 32, "Rotation": 56}.get(item.outputAttribute, -1)
			if offset < 0:
				fail("Unknown math output attribute: " + str(item.outputAttribute))
				continue
			check_values("%s[%d]" % [id, i], output, i * 80 + offset, item.expected,
				reference.absoluteTolerance, reference.relativeTolerance)
			check_alive(id, output, i)
	print("Math: %d cases / %d kernels" % [cases.size(), groups.size()])


func run_noise(reference: Dictionary) -> void:
	var cases: Array = reference.cases
	if cases.size() != 16:
		fail("Unexpected canonical noise coverage: %d" % cases.size())
		return
	var output := dispatch("noise.noise.lgpk", cases)
	if output.is_empty():
		return
	var curls := {}
	for i in cases.size():
		var item: Dictionary = cases[i]
		var label: String = item.label
		for spec in [["expectedPosition", 0], ["expectedVelocity", 16], ["expectedColor", 32], ["expectedSize", 48]]:
			check_values(label + "." + spec[0], output, i * 80 + spec[1], item[spec[0]],
				reference.floatTolerance, 0.0)
		check_alive(label, output, i)
		curls[label] = Vector3(output.decode_float(i * 80), output.decode_float(i * 80 + 4),
			output.decode_float(i * 80 + 8))
	var divergence: float = ((curls.divergence_plus_x.x - curls.divergence_minus_x.x) +
		(curls.divergence_plus_y.y - curls.divergence_minus_y.y) +
		(curls.divergence_plus_z.z - curls.divergence_minus_z.z)) / (2.0 * reference.finiteDifferenceEpsilon)
	if not is_finite(divergence) or absf(divergence) > reference.divergenceTolerance:
		fail("Curl divergence outside tolerance: %s" % divergence)
	var seed_delta: Vector3 = (curls.positive - curls.seed_one).abs()
	if maxf(maxf(seed_delta.x, seed_delta.y), seed_delta.z) <= reference.floatTolerance:
		fail("Noise seed variation is not observable")
	var guard := dispatch("noiseguards.noiseGuards.lgpk", [cases[0]])
	if not guard.is_empty():
		check_values("noise guards position", guard, 0, [0.0, 0.0, 0.0], reference.floatTolerance, 0.0)
		check_values("noise guards velocity", guard, 16, [0.0, 0.0, 0.0], reference.floatTolerance, 0.0)
		check_alive("noise guards", guard, 0)
	print("Noise: %d cases, guards, seed variation, curl divergence %s" % [cases.size(), str(divergence)])


func dispatch(packet_name: String, cases: Array) -> PackedByteArray:
	var packet := FileAccess.get_file_as_bytes("res://" + packet_name)
	if packet.size() < 64 or packet.decode_u32(20) != 1:
		fail("Missing or non-update packet: " + packet_name)
		return PackedByteArray()
	var data := PackedByteArray()
	data.resize(cases.size() * 80)
	data.fill(0)
	for i in cases.size():
		var item: Dictionary = cases[i]
		for j in 3:
			data.encode_float(i * 80 + 4 * j, item.get("inputPosition", [0, 0, 0])[j])
			data.encode_float(i * 80 + 16 + 4 * j, item.get("inputVelocity", [0, 0, 0])[j])
		data.encode_float(i * 80 + 28, 1000.0)
		for j in 4:
			data.encode_float(i * 80 + 32 + 4 * j, item.get("inputColor", [1, 1, 1, 1])[j])
		data.encode_float(i * 80 + 48, 1.0)
		data.encode_float(i * 80 + 52, 1.0)
		data.encode_u32(i * 80 + 64, int(item.get("seed", 0)))
		data.encode_u32(i * 80 + 68, 1)
	var flow := PackedByteArray()
	flow.resize(1024)
	flow.fill(0)
	var buffers: Array[RID] = [
		device.storage_buffer_create(data.size(), data),
		device.storage_buffer_create(packet.size(), packet),
		device.storage_buffer_create(flow.size(), flow)]
	var uniforms: Array[RDUniform] = []
	for binding in 3:
		var uniform := RDUniform.new()
		uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_STORAGE_BUFFER
		uniform.binding = binding
		uniform.add_id(buffers[binding])
		uniforms.append(uniform)
	var uniform_set := device.uniform_set_create(uniforms, shader, 0)
	if not uniform_set.is_valid():
		fail("Uniform set creation failed: " + packet_name)
		for buffer in buffers:
			device.free_rid(buffer)
		return PackedByteArray()
	var push := PackedByteArray()
	push.resize(48)
	push.fill(0)
	push.encode_u32(0, cases.size())
	push.encode_u32(4, cases.size())
	var list := device.compute_list_begin()
	device.compute_list_bind_compute_pipeline(list, pipeline)
	device.compute_list_bind_uniform_set(list, uniform_set, 0)
	device.compute_list_set_push_constant(list, push, push.size())
	device.compute_list_dispatch(list, (cases.size() + 63) / 64, 1, 1)
	device.compute_list_end()
	device.submit()
	device.sync()
	var output := device.buffer_get_data(buffers[0])
	device.free_rid(uniform_set)
	for buffer in buffers:
		device.free_rid(buffer)
	if output.size() != data.size():
		fail("Readback size mismatch: " + packet_name)
		return PackedByteArray()
	return output


func check_values(label: String, output: PackedByteArray, offset: int, expected: Array,
		absolute: float, relative: float) -> void:
	for lane in expected.size():
		var actual := output.decode_float(offset + lane * 4)
		var target: float = expected[lane]
		comparisons += 1
		if not is_finite(actual) or absf(actual - target) > absolute + relative * absf(target):
			fail("%s[%d]: GPU %s expected %s (abs %s rel %s)" %
				[label, lane, str(actual), str(target), str(absolute), str(relative)])


func check_alive(label: String, output: PackedByteArray, index: int) -> void:
	if output.decode_u32(index * 80 + 68) != 1:
		fail(label + ": particle is not alive")


func fail(message: String) -> void:
	failures += 1
	push_error(message)
