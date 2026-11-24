@tool
extends EditorPlugin
## Godot EditorPlugin providing .lfxb import and an isolated LIBERA FX Live Preview dock.

const ImportPluginScript := preload("liberafx_import_plugin.gd")
const LivePreviewScript := preload("liberafx_live_preview.gd")

var _import_plugin: EditorImportPlugin
var _dock: Control
var _invitation_edit: TextEdit
var _pem_edit: TextEdit
var _status_label: Label
var _connect_btn: Button
var _disconnect_btn: Button
var _preview_viewport: SubViewport
var _preview_player: Node
var _preview_client: Node


func _enter_tree() -> void:
	_import_plugin = ImportPluginScript.new()
	add_import_plugin(_import_plugin)
	_build_dock()
	add_control_to_bottom_panel(_dock, "LIBERA FX")


func _exit_tree() -> void:
	_disconnect_preview()
	if _dock != null:
		remove_control_from_bottom_panel(_dock)
		_dock.queue_free()
		_dock = null
	if _import_plugin != null:
		remove_import_plugin(_import_plugin)
		_import_plugin = null


func _build_dock() -> void:
	_dock = HBoxContainer.new()
	_dock.custom_minimum_size = Vector2(0, 220)

	var left := VBoxContainer.new()
	left.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	left.size_flags_stretch_ratio = 1.0
	_dock.add_child(left)

	_status_label = Label.new()
	_status_label.text = "Disconnected. Paste Invitation JSON and Godot TLS Certificate from LIBERA FX."
	left.add_child(_status_label)

	var inv_label := Label.new()
	inv_label.text = "Manual Connection Invitation JSON:"
	left.add_child(inv_label)

	_invitation_edit = TextEdit.new()
	_invitation_edit.custom_minimum_size = Vector2(0, 56)
	_invitation_edit.placeholder_text = '{"type":"librafx_preview_connection","version":1,...}'
	left.add_child(_invitation_edit)

	var pem_label := Label.new()
	pem_label.text = "Public TLS Certificate PEM:"
	left.add_child(pem_label)

	_pem_edit = TextEdit.new()
	_pem_edit.custom_minimum_size = Vector2(0, 56)
	_pem_edit.placeholder_text = "-----BEGIN CERTIFICATE-----"
	left.add_child(_pem_edit)

	var buttons := HBoxContainer.new()
	left.add_child(buttons)

	var paste_inv_btn := Button.new()
	paste_inv_btn.text = "Paste Invitation"
	paste_inv_btn.pressed.connect(func() -> void: _invitation_edit.text = DisplayServer.clipboard_get().strip_edges())
	buttons.add_child(paste_inv_btn)

	var paste_pem_btn := Button.new()
	paste_pem_btn.text = "Paste Certificate"
	paste_pem_btn.pressed.connect(func() -> void: _pem_edit.text = DisplayServer.clipboard_get().strip_edges())
	buttons.add_child(paste_pem_btn)

	_connect_btn = Button.new()
	_connect_btn.text = "Connect to LIBERA FX"
	_connect_btn.pressed.connect(_on_connect_pressed)
	buttons.add_child(_connect_btn)

	_disconnect_btn = Button.new()
	_disconnect_btn.text = "Disconnect"
	_disconnect_btn.disabled = true
	_disconnect_btn.pressed.connect(_disconnect_preview)
	buttons.add_child(_disconnect_btn)

	var vp_container := SubViewportContainer.new()
	vp_container.stretch = true
	vp_container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	vp_container.size_flags_stretch_ratio = 1.0
	vp_container.custom_minimum_size = Vector2(280, 200)
	_dock.add_child(vp_container)

	_preview_viewport = SubViewport.new()
	_preview_viewport.own_world_3d = true
	_preview_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	vp_container.add_child(_preview_viewport)

	var cam := Camera3D.new()
	_preview_viewport.add_child(cam)
	cam.look_at_from_position(Vector3(0, 1.5, 4.5), Vector3.ZERO, Vector3.UP)

	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-45, 35, 0)
	_preview_viewport.add_child(light)


func _on_connect_pressed() -> void:
	_disconnect_preview()
	if not ClassDB.class_exists(&"LibraEffectPlayer"):
		_status_label.text = "LibraEffectPlayer GDExtension class is not loaded."
		return
	_preview_player = ClassDB.instantiate(&"LibraEffectPlayer")
	_preview_viewport.add_child(_preview_player)

	_preview_client = LivePreviewScript.new()
	_preview_viewport.add_child(_preview_client)
	_preview_client.connect("connection_closed", _on_connection_closed)
	_preview_client.connect("preview_error", _on_preview_error)
	_preview_client.connect("package_preview_ready", _on_package_ready)

	var err: Error = _preview_client.call("connect_package_local", _preview_player,
			_invitation_edit.text.strip_edges(), _pem_edit.text.strip_edges())
	if err != OK:
		_status_label.text = "Connection failed (Error %d)." % err
		_disconnect_preview()
		return
	_status_label.text = "Connecting to LIBERA FX..."
	_connect_btn.disabled = true
	_disconnect_btn.disabled = false


func _disconnect_preview() -> void:
	if _preview_client != null and is_instance_valid(_preview_client):
		_preview_client.call("disconnect_preview")
		_preview_client.queue_free()
	_preview_client = null
	if _preview_player != null and is_instance_valid(_preview_player):
		_preview_player.call("stop")
		_preview_player.queue_free()
	_preview_player = null
	if _connect_btn != null:
		_connect_btn.disabled = false
	if _disconnect_btn != null:
		_disconnect_btn.disabled = true
	if _status_label != null and not _status_label.text.begins_with("Connection failed"):
		_status_label.text = "Disconnected."


func _on_connection_closed() -> void:
	_disconnect_preview()
	_status_label.text = "Connection closed."


func _on_preview_error(message: String) -> void:
	_status_label.text = "Preview error: " + message


func _on_package_ready(sequence: int) -> void:
	_status_label.text = "Connected · Previewing package #%d" % sequence
