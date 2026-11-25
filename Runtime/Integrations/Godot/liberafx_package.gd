@tool
class_name LibraFxPackage
extends Resource
## Imported compiled LIBERA FX package (.lfxp).

@export var package_bytes: PackedByteArray = PackedByteArray()
@export var source_file: String = ""
@export var auto_resolved_resources: Dictionary = {}
@export var resource_overrides: Dictionary = {}
