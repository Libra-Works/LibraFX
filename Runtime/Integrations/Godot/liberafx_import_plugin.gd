@tool
extends EditorImportPlugin
## Imports .lfxp compiled effect packages as LibraFxPackage resources.

const PackageScript := preload("liberafx_package.gd")


func _get_importer_name() -> String:
	return "liberafx.package"


func _get_visible_name() -> String:
	return "LIBERA FX Package"


func _get_recognized_extensions() -> PackedStringArray:
	return PackedStringArray(["lfxp"])


func _get_save_extension() -> String:
	return "res"


func _get_resource_type() -> String:
	return "Resource"


func _get_preset_count() -> int:
	return 1


func _get_preset_name(_preset_index: int) -> String:
	return "Default"


func _get_import_options(_path: String, _preset_index: int) -> Array[Dictionary]:
	return []


func _get_option_visibility(_path: String, _option_name: StringName, _options: Dictionary) -> bool:
	return true


func _import(source_file: String, save_path: String, _options: Dictionary,
		_platform_variants: Array[String], _gen_files: Array[String]) -> Error:
	var bytes := FileAccess.get_file_as_bytes(source_file)
	if bytes.size() < 24:
		return ERR_FILE_CORRUPT
	var magic := bytes.slice(0, 4).get_string_from_ascii()
	if magic != "LFXP":
		return ERR_INVALID_DATA
	var pkg: Resource = PackageScript.new()
	pkg.set("package_bytes", bytes)
	pkg.set("source_file", source_file)
	pkg.set("auto_resolved_resources", _resolve_res_paths(bytes))
	return ResourceSaver.save(pkg, "%s.%s" % [save_path, _get_save_extension()])


func _resolve_res_paths(bytes: PackedByteArray) -> Dictionary:
	var resolved: Dictionary = {}
	var size := bytes.size()
	var i := 0
	while i + 6 < size:
		if bytes[i] == 114 and bytes[i + 1] == 101 and bytes[i + 2] == 115 and bytes[i + 3] == 58 and bytes[i + 4] == 47 and bytes[i + 5] == 47:
			var j := i + 6
			while j < size:
				var b := bytes[j]
				var ok := (b >= 65 and b <= 90) or (b >= 97 and b <= 122) or (b >= 48 and b <= 57) or b == 95 or b == 46 or b == 47 or b == 45
				if not ok:
					break
				j += 1
			if j > i + 6:
				var path := bytes.slice(i, j).get_string_from_ascii()
				if not resolved.has(path) and ResourceLoader.exists(path):
					var res := ResourceLoader.load(path)
					if res is Mesh or res is Texture2D or res is Material:
						resolved[path] = res
			i = j
			continue
		i += 1
	return resolved
