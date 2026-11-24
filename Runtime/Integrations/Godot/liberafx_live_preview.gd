extends Node
## Connects a local editor to a dedicated LibraEffectPlayer.

signal connection_closed
signal frame_rejected(result: int)
signal preview_error(message: String)
signal package_preview_ready(sequence: int)
signal preview_signal_dispatched(node_id: String, sequence: int)

const MAX_PACKET_BYTES := 16 * 1024 * 1024
const CONNECT_TIMEOUT_MS := 8000

var _peer: WebSocketPeer
var _player: Node
var _deadline: int
var _resync_pending := false
var _package_preview: RefCounted
var _resource_adapter: Script
var _hello_sent := false


func connect_local(player: Node, invitation: String, public_pem: String) -> Error:
	return _connect_local(player, invitation, public_pem, false)


## Uses a dedicated, idle player with its asset bindings already configured.
func connect_package_local(player: Node, invitation: String, public_pem: String) -> Error:
	return _connect_local(player, invitation, public_pem, true)


func _connect_local(player: Node, invitation: String, public_pem: String, package_mode: bool) -> Error:
	if not is_inside_tree() or not is_instance_valid(player) or not player.is_inside_tree() \
			or not player.has_method("apply_preview_frame") or not player.has_method("reset_preview_stream"):
		return ERR_INVALID_PARAMETER
	if package_mode and (not player.has_method("play_package") or not player.has_method("advance") \
			or not player.has_method("get_playback_generation") or not player.has_method("is_playing") \
			or not player.has_method("get_gpu_error") or not player.has_method("stop")):
		return ERR_UNAVAILABLE
	if package_mode and player.call("is_playing"):
		return ERR_BUSY
	var package_script: Script
	if package_mode:
		package_script = load(get_script().resource_path.get_base_dir().path_join("liberafx_package_preview.gd"))
		if package_script == null:
			return ERR_FILE_NOT_FOUND
	var info: Variant = JSON.parse_string(invitation)
	if not info is Dictionary or info.get("type") != "librafx_preview_connection" or info.get("version") != 1:
		return ERR_INVALID_DATA
	var host: String = str(info.get("host", ""))
	var port_value: Variant = info.get("port")
	if host not in ["localhost", "127.0.0.1"] or typeof(port_value) not in [TYPE_INT, TYPE_FLOAT]:
		return ERR_INVALID_DATA
	var port: int = int(port_value)
	if port != port_value or port < 1 or port > 65535:
		return ERR_INVALID_DATA
	var token: String = str(info.get("accessToken", ""))
	var fingerprint: String = str(info.get("certificateFingerprint", "")).to_lower()
	if not _is_hex64(token) or not _is_hex64(fingerprint):
		return ERR_INVALID_DATA

	const START := "-----BEGIN CERTIFICATE-----"
	const END := "-----END CERTIFICATE-----"
	var normalized := public_pem.strip_edges()
	if normalized.length() > 8192 or not normalized.begins_with(START) or not normalized.ends_with(END):
		return ERR_INVALID_DATA
	var base64 := normalized.substr(START.length(), normalized.length() - START.length() - END.length())
	if base64.contains("-"):
		return ERR_INVALID_DATA
	var der := Marshalls.base64_to_raw(base64.replace("\r", "").replace("\n", ""))
	if der.is_empty():
		return ERR_INVALID_DATA
	var hash := HashingContext.new()
	if hash.start(HashingContext.HASH_SHA256) != OK or hash.update(der) != OK:
		return ERR_INVALID_DATA
	if hash.finish().hex_encode() != fingerprint:
		return ERR_UNAUTHORIZED
	var trust := X509Certificate.new()
	if trust.load_from_string(normalized) != OK:
		return ERR_INVALID_DATA

	var socket := WebSocketPeer.new()
	socket.inbound_buffer_size = MAX_PACKET_BYTES
	socket.max_queued_packets = 8
	socket.handshake_headers = PackedStringArray(["X-LiberaFx-Preview-Token: " + token])
	# The editor listener is IPv4; localhost may resolve to ::1 first.
	var url := "wss://127.0.0.1:%d/preview?protocol=3" % port
	if package_mode:
		url += "&godotPackagePreview=1"
	var result := socket.connect_to_url(url,
			TLSOptions.client(trust, "localhost"))
	if result != OK:
		return result
	disconnect_preview()
	_player = player
	_player.call("reset_preview_stream")
	if package_mode:
		_package_preview = package_script.new()
		_package_preview.call("initialize", player)
	var adapter_path: String = (get_script() as Script).resource_path.get_base_dir().path_join("liberafx_resource_adapter.gd")
	if ResourceLoader.exists(adapter_path):
		_resource_adapter = load(adapter_path) as Script
	_peer = socket
	_deadline = Time.get_ticks_msec() + CONNECT_TIMEOUT_MS
	_hello_sent = false
	set_process(true)
	return OK


func disconnect_preview() -> void:
	if _peer != null:
		_peer.close()
		_peer = null
	if _package_preview != null:
		_package_preview.call("close")
		_package_preview = null
	if is_instance_valid(_player):
		_player.call("reset_preview_stream")
	_player = null
	_resync_pending = false
	_hello_sent = false
	set_process(false)


func _process(_delta: float) -> void:
	if _peer == null:
		return
	_peer.poll()
	var state := _peer.get_ready_state()
	if not is_instance_valid(_player) or state == WebSocketPeer.STATE_CLOSED or (
			state == WebSocketPeer.STATE_CONNECTING and Time.get_ticks_msec() > _deadline):
		disconnect_preview()
		connection_closed.emit()
		return
	if state != WebSocketPeer.STATE_OPEN:
		return
	if _package_preview != null and not _hello_sent:
		var caps := ["godotPackagePreview"]
		if _resource_adapter != null:
			caps.append("assets")
			caps.append("resourceIdentity")
		var hello := JSON.stringify({"type": "hello", "engine": "Godot", "role": "editor",
				"version": 1, "capabilities": caps})
		if _peer.send_text(hello) != OK:
			disconnect_preview()
			connection_closed.emit()
			return
		_hello_sent = true
	for _packet in range(8):
		if _peer.get_available_packet_count() == 0:
			break
		var bytes := _peer.get_packet()
		if _peer.was_string_packet():
			if _resource_adapter != null and bytes.size() <= MAX_PACKET_BYTES:
				var reply := str(_resource_adapter.call("process_request", bytes.get_string_from_utf8()))
				if not reply.is_empty() and _peer.send_text(reply) != OK:
					disconnect_preview()
					connection_closed.emit()
					return
				continue
			disconnect_preview()
			frame_rejected.emit(ERR_INVALID_DATA)
			return
		if bytes.size() > MAX_PACKET_BYTES:
			disconnect_preview()
			frame_rejected.emit(ERR_INVALID_DATA)
			return
		if _package_preview != null:
			var outcome: Dictionary = _package_preview.call("consume", bytes)
			if outcome.has("diagnostic"):
				preview_error.emit(str(outcome["diagnostic"]))
			var package_result: int = int(outcome["result"])
			if package_result != 0:
				preview_error.emit(str(_package_preview.get("error")))
				frame_rejected.emit(package_result)
				return
			for signal_request in outcome.get("signals", []):
				preview_signal_dispatched.emit(
						str(signal_request["nodeId"]), int(signal_request["sequence"]))
			if outcome["resync"]:
				if not _resync_pending:
					if _peer.send_text('{"type":"resync"}') != OK:
						disconnect_preview()
						connection_closed.emit()
						return
					_resync_pending = true
			elif outcome.get("snapshot", false):
				_resync_pending = false
				var ack := JSON.stringify({"type": "package_preview_ack",
						"programSequence": outcome["sequence"]})
				if _peer.send_text(ack) != OK:
					disconnect_preview()
					connection_closed.emit()
					return
				package_preview_ready.emit(int(outcome["sequence"]))
			continue
		var result: int = _player.call("apply_preview_frame", bytes)
		if result == 6: # Core Result::QueueRejected means the delta requires a new baseline.
			if not _resync_pending:
				var send_result := _peer.send_text('{"type":"resync"}')
				if send_result != OK:
					disconnect_preview()
					connection_closed.emit()
					return
				_resync_pending = true
		elif result != 0:
			frame_rejected.emit(result)
			return
		elif bytes.size() >= 48 and bytes.decode_u16(6) in [1, 3]:
			_resync_pending = false
	if _package_preview != null:
		var tick_result: int = _package_preview.call("tick")
		if tick_result != 0:
			preview_error.emit(str(_package_preview.get("error")))
			frame_rejected.emit(tick_result)
			disconnect_preview()


func _exit_tree() -> void:
	disconnect_preview()


func _is_hex64(text: String) -> bool:
	if text.length() != 64:
		return false
	for character in text:
		if character.to_lower() not in "0123456789abcdef":
			return false
	return true
