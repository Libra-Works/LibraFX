extends RefCounted
## Handles Content Window asset browsing and Resource Identity queries over res://.

const PROJECT_ID_SETTING := "liberafx/resource_identity/project_id"
const MAX_SCAN_ASSETS := 1024


static func process_request(request_json: String) -> String:
	var parsed: Variant = JSON.parse_string(request_json)
	if not parsed is Dictionary:
		return JSON.stringify({"type": "asset_result", "operation": "invalid", "error": "Invalid JSON request."})
	var req: Dictionary = parsed
	var req_type := str(req.get("type", ""))
	var req_id := str(req.get("requestId", ""))
	if req_id.is_empty():
		return JSON.stringify({"type": "asset_result", "operation": "invalid", "error": "Missing requestId."})
	match req_type:
		"asset_catalog":
			return _handle_folders(req, req_id) if bool(req.get("foldersOnly", false)) else _handle_catalog(req, req_id)
		"asset_details":
			return _handle_details(req, req_id)
		"asset_thumbnail":
			return _handle_thumbnail(req, req_id)
		"resource_identity_project":
			return _handle_project(req, req_id)
		"resource_identity_probe":
			return _handle_probe(req, req_id)
		"resource_identity_schema":
			return _handle_schema(req, req_id)
		"resource_identity_parameter_probe":
			return _handle_parameter_probe(req, req_id)
		_:
			return JSON.stringify({"type": "asset_result", "requestId": req_id, "operation": "invalid", "error": "Unsupported request type."})


static func _handle_folders(req: Dictionary, req_id: String) -> String:
	var folder := str(req.get("folder", "")).strip_edges()
	var offset := maxi(0, int(req.get("offset", 0)))
	var limit := clampi(int(req.get("limit", 64)), 1, 64)
	var entries: Array = []
	if folder.is_empty():
		entries.append({"path": "res://", "name": "res://"})
	elif DirAccess.dir_exists_absolute(folder):
		var dir := DirAccess.open(folder)
		if dir != null:
			var subdirs := Array(dir.get_directories())
			subdirs.sort()
			for sub in subdirs:
				if sub.begins_with("."):
					continue
				var full := (folder.trim_suffix("/") + "/" + sub) if folder != "res://" else ("res://" + sub)
				entries.append({"path": full, "name": sub})
	var page := entries.slice(offset, mini(entries.size(), offset + limit))
	return JSON.stringify({
		"type": "asset_result",
		"requestId": req_id,
		"operation": "catalog",
		"folder": folder,
		"total": entries.size(),
		"offset": offset,
		"folders": page
	})


static func _handle_catalog(req: Dictionary, req_id: String) -> String:
	var folder := str(req.get("folder", "")).strip_edges()
	var query := str(req.get("query", "")).strip_edges().to_lower()
	var asset_class := str(req.get("assetClass", "")).strip_edges()
	var offset := maxi(0, int(req.get("offset", 0)))
	var limit := clampi(int(req.get("limit", 32)), 1, 64)
	var root := "res://" if folder.is_empty() else folder
	var matched: Array = []
	if DirAccess.dir_exists_absolute(root):
		_scan_dir(root, query, asset_class, matched)
	matched.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return str(a["path"]) < str(b["path"]))
	var page := matched.slice(offset, mini(matched.size(), offset + limit))
	return JSON.stringify({
		"type": "asset_result",
		"requestId": req_id,
		"operation": "catalog",
		"folder": folder,
		"pending": false,
		"total": matched.size(),
		"offset": offset,
		"assets": page
	})


static func _scan_dir(dir_path: String, query: String, asset_class: String, out: Array) -> void:
	if out.size() >= MAX_SCAN_ASSETS:
		return
	var dir := DirAccess.open(dir_path)
	if dir == null:
		return
	for file_name in dir.get_files():
		if out.size() >= MAX_SCAN_ASSETS:
			return
		if file_name.begins_with(".") or file_name.ends_with(".import") or file_name.ends_with(".uid"):
			continue
		var full := (dir_path.trim_suffix("/") + "/" + file_name) if dir_path != "res://" else ("res://" + file_name)
		var kind := _classify_extension(file_name.get_extension().to_lower())
		if kind.is_empty():
			continue
		if not asset_class.is_empty() and kind != asset_class:
			continue
		var display_name := file_name.get_basename()
		if not query.is_empty() and not display_name.to_lower().contains(query) and not full.to_lower().contains(query):
			continue
		out.append({
			"name": display_name,
			"path": full,
			"assetClass": kind,
			"engine": "Godot",
			"materials": []
		})
	for sub in dir.get_directories():
		if sub.begins_with("."):
			continue
		var next := (dir_path.trim_suffix("/") + "/" + sub) if dir_path != "res://" else ("res://" + sub)
		_scan_dir(next, query, asset_class, out)


static func _classify_extension(ext: String) -> String:
	match ext:
		"mesh", "obj", "glb", "gltf", "tscn", "scn":
			return "model"
		"png", "jpg", "jpeg", "webp", "exr", "svg":
			return "texture"
		"material", "tres":
			return "material"
		_:
			return ""


static func _handle_details(req: Dictionary, req_id: String) -> String:
	var path := str(req.get("path", ""))
	if not path.begins_with("res://") or not ResourceLoader.exists(path):
		return JSON.stringify({"type": "asset_result", "requestId": req_id, "operation": "details", "error": "Godot resource not found: " + path})
	var res := ResourceLoader.load(path)
	if res == null:
		return JSON.stringify({"type": "asset_result", "requestId": req_id, "operation": "details", "error": "Failed to load Godot resource: " + path})
	var kind := _classify_resource(res)
	if kind.is_empty():
		return JSON.stringify({"type": "asset_result", "requestId": req_id, "operation": "details", "error": "Unsupported Godot resource type: " + path})
	var width := 0
	var height := 0
	var materials: Array = []
	if res is Texture2D:
		width = (res as Texture2D).get_width()
		height = (res as Texture2D).get_height()
	elif res is Material:
		materials.append(_build_material_slot(0, res as Material))
	elif res is Mesh:
		var mesh := res as Mesh
		for i in range(mini(mesh.get_surface_count(), 128)):
			var mat := mesh.surface_get_material(i)
			if mat != null:
				materials.append(_build_material_slot(i, mat))
	return JSON.stringify({
		"type": "asset_result",
		"requestId": req_id,
		"operation": "details",
		"asset": {
			"name": path.get_file().get_basename(),
			"path": path,
			"assetClass": kind,
			"engine": "Godot",
			"width": width,
			"height": height,
			"materials": materials
		}
	})


static func _handle_thumbnail(req: Dictionary, req_id: String) -> String:
	var path := str(req.get("path", ""))
	var size := clampi(int(req.get("size", 128)), 16, 256)
	if not path.begins_with("res://") or not ResourceLoader.exists(path):
		return JSON.stringify({"type": "asset_result", "requestId": req_id, "operation": "thumbnail", "error": "Godot resource not found: " + path})
	var res := ResourceLoader.load(path)
	var image: Image = null
	if res is Texture2D:
		var tex_img := (res as Texture2D).get_image()
		if tex_img != null:
			image = tex_img.duplicate()
			image.convert(Image.FORMAT_RGBA8)
			image.resize(size, size, Image.INTERPOLATE_BILINEAR)
	if image == null:
		image = Image.create(size, size, false, Image.FORMAT_RGBA8)
		image.fill(Color(0.22, 0.25, 0.30, 1.0))
	var png := image.save_png_to_buffer()
	return JSON.stringify({
		"type": "asset_result",
		"requestId": req_id,
		"operation": "thumbnail",
		"path": path,
		"mime": "image/png",
		"width": size,
		"height": size,
		"imageBase64": Marshalls.raw_to_base64(png)
	})


static func _handle_project(req: Dictionary, req_id: String) -> String:
	var operation := str(req.get("operation", "status"))
	if operation == "register":
		if str(ProjectSettings.get_setting(PROJECT_ID_SETTING, "")).is_empty():
			ProjectSettings.set_setting(PROJECT_ID_SETTING, _deterministic_project_uuid())
			ProjectSettings.save()
	elif operation == "clear":
		ProjectSettings.set_setting(PROJECT_ID_SETTING, "")
		ProjectSettings.save()
	var pid := str(ProjectSettings.get_setting(PROJECT_ID_SETTING, ""))
	var state := "Unregistered" if pid.is_empty() else "Registered"
	return JSON.stringify({
		"type": "resource_identity_result",
		"requestId": req_id,
		"operation": operation,
		"status": state,
		"projectState": state,
		"project": {"engine": "Godot", "projectId": pid},
		"detail": ""
	})


static func _handle_probe(req: Dictionary, req_id: String) -> String:
	var pid := str(ProjectSettings.get_setting(PROJECT_ID_SETTING, ""))
	var binding: Dictionary = req.get("binding", {})
	var value := str(binding.get("value", ""))
	if pid.is_empty() or pid != str(req.get("projectId", "")):
		return _probe_reply(req_id, "probe", pid, "Offline", "", "Godot project identity is unregistered or mismatched.")
	if not value.begins_with("res://") or not ResourceLoader.exists(value):
		return _probe_reply(req_id, "probe", pid, "Missing", "", "Godot resource not found: " + value)
	var res := ResourceLoader.load(value)
	var kind := _classify_resource(res)
	if kind.is_empty():
		return _probe_reply(req_id, "probe", pid, "TypeMismatch", "", "Unsupported Godot resource type.")
	return _probe_reply(req_id, "probe", pid, "Resolved", kind, "")


static func _handle_schema(req: Dictionary, req_id: String) -> String:
	var pid := str(ProjectSettings.get_setting(PROJECT_ID_SETTING, ""))
	var binding: Dictionary = req.get("resourceBinding", {})
	var value := str(binding.get("value", ""))
	if pid.is_empty() or pid != str(req.get("projectId", "")):
		return _probe_reply(req_id, "schema", pid, "Offline", "", "Godot project identity is unregistered or mismatched.")
	var res := ResourceLoader.load(value) if value.begins_with("res://") and ResourceLoader.exists(value) else null
	if not res is ShaderMaterial:
		return _probe_reply(req_id, "schema", pid, "TypeMismatch", "", "Godot material schema requires a ShaderMaterial.")
	var mat := res as ShaderMaterial
	var params: Array = []
	if mat.shader != null:
		for prop in mat.shader.get_shader_uniform_list():
			var name := str(prop.get("name", ""))
			if not name.is_empty():
				params.append({"name": name, "kind": _uniform_kind(int(prop.get("type", 0)))})
	return JSON.stringify({
		"type": "resource_identity_result",
		"requestId": req_id,
		"operation": "schema",
		"status": "Resolved",
		"actualKind": "material",
		"project": {"engine": "Godot", "projectId": pid},
		"schemaJson": JSON.stringify({"parameters": params}),
		"detail": ""
	})


static func _handle_parameter_probe(req: Dictionary, req_id: String) -> String:
	var pid := str(ProjectSettings.get_setting(PROJECT_ID_SETTING, ""))
	var res_binding: Dictionary = req.get("resourceBinding", {})
	var param_binding: Dictionary = req.get("parameterBinding", {})
	var res_path := str(res_binding.get("value", ""))
	var res := ResourceLoader.load(res_path) if res_path.begins_with("res://") and ResourceLoader.exists(res_path) else null
	if not res is ShaderMaterial:
		return _probe_reply(req_id, "parameterProbe", pid, "Missing", "", "Target ShaderMaterial not found.")
	var mat := res as ShaderMaterial
	var p_parsed: Variant = JSON.parse_string(str(param_binding.get("value", "{}")))
	var param_name := str(p_parsed.get("name", "")) if p_parsed is Dictionary else ""
	if mat.shader != null:
		for prop in mat.shader.get_shader_uniform_list():
			if str(prop.get("name", "")) == param_name:
				return _probe_reply(req_id, "parameterProbe", pid, "Resolved", _uniform_kind(int(prop.get("type", 0))), "")
	return _probe_reply(req_id, "parameterProbe", pid, "Missing", "", "Shader uniform not found: " + param_name)


static func _probe_reply(req_id: String, op: String, pid: String, status: String, actual_kind: String, detail: String) -> String:
	return JSON.stringify({
		"type": "resource_identity_result",
		"requestId": req_id,
		"operation": op,
		"status": status,
		"actualKind": actual_kind,
		"project": {"engine": "Godot", "projectId": pid},
		"detail": detail
	})


static func _classify_resource(res: Resource) -> String:
	if res is Mesh or res is PackedScene:
		return "model"
	if res is Texture2D:
		return "texture"
	if res is Material:
		return "material"
	return ""


static func _build_material_slot(slot_idx: int, mat: Material) -> Dictionary:
	var params: Array = []
	if mat is ShaderMaterial:
		var sm := mat as ShaderMaterial
		if sm.shader != null:
			for prop in sm.shader.get_shader_uniform_list():
				var uname := str(prop.get("name", ""))
				if uname.is_empty():
					continue
				var uval: Variant = sm.get_shader_parameter(uname)
				if uval is float or uval is int:
					params.append({"name": uname, "kind": "scalar", "value": float(uval)})
				elif uval is Vector4:
					var v4 := uval as Vector4
					params.append({"name": uname, "kind": "vector", "value": [v4.x, v4.y, v4.z, v4.w]})
				elif uval is Color:
					var c := uval as Color
					params.append({"name": uname, "kind": "vector", "value": [c.r, c.g, c.b, c.a]})
				elif uval is Texture2D:
					var tpath := (uval as Texture2D).resource_path
					params.append({"name": uname, "kind": "texture", "value": null if tpath.is_empty() else tpath})
	return {
		"slot": slot_idx,
		"name": mat.resource_name if not mat.resource_name.is_empty() else "Material",
		"path": mat.resource_path if not mat.resource_path.is_empty() else "res://__InlineMaterial",
		"parameters": params
	}


static func _uniform_kind(t: int) -> String:
	match t:
		TYPE_FLOAT, TYPE_INT:
			return "scalar"
		TYPE_VECTOR4, TYPE_COLOR, TYPE_VECTOR3, TYPE_VECTOR2:
			return "vector"
		_:
			return "texture"


static func _deterministic_project_uuid() -> String:
	var raw := ProjectSettings.globalize_path("res://").sha256_text()
	return "%s-%s-4%s-a%s-%s" % [raw.substr(0, 8), raw.substr(8, 4), raw.substr(12, 3), raw.substr(15, 3), raw.substr(18, 12)]
