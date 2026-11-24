extends RefCounted
## Protocol-3 package receiver. The supplied player is exclusively reserved for preview.

const HEADER_BYTES := 48
const MAX_SIGNAL_BYTES := 65536
const MAX_SIGNAL_REQUESTS := 32
const STEP := 1.0 / 60.0
const STEPS_PER_FRAME := 60
const MAX_CATCHUP_MS := 8.0

var _player: Node
var _generation: int = -1
var _sequence: int = -1
var _signal_sequence: int = -1
var _has_program := false
var _package := PackedByteArray()
var _time := 0.0
var _target := 0.0
var _finished := false
var _seeking := false
var error := ""


func is_seeking() -> bool:
	return _seeking


func initialize(player: Node) -> void:
	_player = player


func close() -> void:
	if _owns_player() and _player.call("is_playing"):
		_player.call("stop")
	_player = null
	_package = PackedByteArray()
	_generation = -1
	_sequence = -1
	_signal_sequence = -1
	_has_program = false
	_time = 0.0
	_target = 0.0
	_finished = false
	_seeking = false


func _owns_player() -> bool:
	return is_instance_valid(_player) and _generation >= 0 \
			and int(_player.call("get_playback_generation")) == _generation


func _is_uuid(text: String) -> bool:
	if text.length() != 36:
		return false
	for index in range(text.length()):
		var character := text.substr(index, 1).to_lower()
		if index in [8, 13, 18, 23]:
			if character != "-":
				return false
		elif character not in "0123456789abcdef":
			return false
	return true


func _parse_nonnegative_i64(text: String) -> int:
	if text.is_empty() or text.length() > 19 or (text.length() > 1 and text.begins_with("0")):
		return -1
	for index in range(text.length()):
		if text.substr(index, 1) not in "0123456789":
			return -1
	if text.length() == 19 and text > "9223372036854775807":
		return -1
	return text.to_int()


func _fail(message: String, result: int = ERR_INVALID_DATA) -> Dictionary:
	error = message
	return {"result": result, "resync": false}


func consume(bytes: PackedByteArray) -> Dictionary:
	if bytes.size() < HEADER_BYTES or bytes.slice(0, 4).get_string_from_ascii() != "LFXP" \
			or bytes.decode_u16(4) != 1:
		return _fail("Invalid preview frame.")
	var kind := bytes.decode_u16(6)
	var sequence := bytes.decode_u64(8)
	var base := bytes.decode_u64(16)
	var time := bytes.decode_double(24)
	var rate := bytes.decode_double(32)
	var flags := bytes.decode_u32(40)
	var length := bytes.decode_u32(44)
	if kind < 1 or kind > 5 or sequence < 0 or base < 0 or length != bytes.size() - HEADER_BYTES \
			or not is_finite(time) or time < 0 \
			or not is_finite(rate) or rate < 0 or (flags & ~3) != 0:
		return _fail("Invalid or unsupported preview frame.")
	if (kind == 1 and (base != 0 or length == 0)) \
			or (kind == 2 and length != 0) \
			or (kind == 3 and (base != 0 or length > 4096)) \
			or (kind == 4 and (sequence != 0 or base != 0 or time != 0.0 or rate != 0.0 \
					or flags != 0 or length == 0 or length > MAX_SIGNAL_BYTES)):
		return _fail("Invalid preview frame payload.")
	if kind == 4:
		var control: Variant = JSON.parse_string(bytes.slice(HEADER_BYTES).get_string_from_utf8())
		if control is Dictionary and control.get("type") == "preview_motion" and control.size() == 2:
			var offset: Variant = control.get("offset")
			if not offset is Array or offset.size() != 3:
				return _fail("Invalid preview motion offset.")
			for lane in offset:
				if not (lane is float or lane is int) or not is_finite(float(lane)) or abs(float(lane)) > 1000.0:
					return _fail("Invalid preview motion offset.")
			if _owns_player():
				var result: int = _player.call("set_preview_offset", PackedFloat64Array(offset))
				if result != 0:
					return _fail("Cannot move the preview effect.", result)
			return {"result": 0, "resync": false}
		if control is Dictionary and control.get("type") == "preview_reset" and control.size() == 1:
			if _owns_player():
				_player.call("stop")
			_package = PackedByteArray()
			_generation = -1
			_sequence = -1
			_signal_sequence = -1
			_has_program = false
			_time = 0.0
			_target = 0.0
			_finished = false
			_seeking = false
			return {"result": 0, "resync": false, "reset": true}
		return _consume_signals(bytes.slice(HEADER_BYTES))
	if sequence < _sequence:
		return {"result": 0, "resync": false}
	if kind == 5:
		if not _has_program or not _owns_player():
			return {"result": 0, "resync": true}
		if int(_player.call("apply_parameter_delta", bytes.slice(HEADER_BYTES))) != 0:
			return {"result": 0, "resync": true}
		_sequence = sequence
		return {"result": 0, "resync": false, "sequence": sequence}
	if kind == 2 and (not _has_program or base != _sequence or sequence == _sequence):
		return {"result": 0, "resync": true}
	if kind == 3:
		error = bytes.slice(HEADER_BYTES).get_string_from_utf8()
		_sequence = sequence
		return {"result": 0, "resync": false, "diagnostic": error}
	if kind == 1:
		var package := bytes.slice(HEADER_BYTES)
		# Ignore an LFXB snapshot sent before the server received our capability hello.
		if package.size() >= 4 and package.slice(0, 4).get_string_from_ascii() == "LFXB":
			return {"result": 0, "resync": false}
		if package.size() < 24 or package.slice(0, 4).get_string_from_ascii() != "LFXP":
			return _fail("Expected a compiled .lfxb package (LFXP signature; legacy .lfxp is also supported).")
		if _generation >= 0 and not _owns_player():
			return _fail("Preview player was replaced externally.", ERR_BUSY)
		if _generation < 0 and _player.call("is_playing"):
			return _fail("Preview player is no longer idle.", ERR_BUSY)
		var result: int = _player.call("play_package", package, -1.0)
		if result != 0:
			return _fail("Godot rejected the preview package (result %d): %s" % [
					result, str(_player.call("get_gpu_error"))], result)
		_generation = int(_player.call("get_playback_generation"))
		_player.set_process(false)
		_package = package
		_time = 0.0
		_finished = false
		_signal_sequence = -1
		result = _advance(0.0)
		if result != 0:
			return _fail("Initial package evaluation failed (result %d)." % result, result)
		_has_program = true
	_target = time
	_sequence = sequence
	error = ""
	return {"result": 0, "resync": false, "snapshot": kind == 1, "sequence": sequence}


func _consume_signals(payload: PackedByteArray) -> Dictionary:
	if not _has_program or not _owns_player():
		return _fail("Editor Send arrived without an owned preview package.", ERR_BUSY)
	var parsed: Variant = JSON.parse_string(payload.get_string_from_utf8())
	if not parsed is Dictionary or parsed.size() != 5 \
			or parsed.get("type") != "godot_preview_signals" \
			or typeof(parsed.get("sequence")) != TYPE_STRING \
			or typeof(parsed.get("timeSeconds")) not in [TYPE_INT, TYPE_FLOAT] \
			or typeof(parsed.get("baseSequence")) != TYPE_STRING \
			or not parsed.get("requests") is Array \
			or parsed["requests"].is_empty() or parsed["requests"].size() > MAX_SIGNAL_REQUESTS:
		return _fail("Invalid Editor Send request payload.")
	var sequence := _parse_nonnegative_i64(parsed["sequence"])
	var time: float = float(parsed["timeSeconds"])
	var base := _parse_nonnegative_i64(parsed["baseSequence"])
	if sequence < 0 or base < 0 or base > 9223372036854775807 - MAX_SIGNAL_REQUESTS:
		return _fail("Invalid Editor Send request sequence.")
	if sequence <= _sequence:
		return {"result": 0, "resync": false}
	if not is_finite(time) or time < 0:
		return _fail("Invalid Editor Send request sequence.")
	if _signal_sequence >= 0 and base != _signal_sequence:
		return {"result": 0, "resync": true}
	var requests: Array = parsed["requests"]
	var expected := base
	for request in requests:
		if not request is Dictionary or typeof(request.get("sequence")) != TYPE_STRING \
				or not request.has("nodeId") or typeof(request["nodeId"]) != TYPE_STRING \
				or not _is_uuid(request["nodeId"]):
			return _fail("Invalid Editor Send request entry.")
		expected += 1
		if _parse_nonnegative_i64(request["sequence"]) != expected:
			return _fail("Editor Send request sequence is not contiguous.")
	for request in requests:
		var result: int = _player.call("dispatch_signal", request["nodeId"])
		if result != 0:
			return _fail("Godot rejected Editor Send signal %s (result %d)." % [
					request["nodeId"], result], result)
	var advance_result: int = _player.call("advance", 0.0)
	if advance_result != 0:
		return _fail("Godot failed to evaluate an Editor Send signal (result %d)." % advance_result,
				advance_result)
	_signal_sequence = expected
	_target = time
	_sequence = sequence
	error = ""
	return {"result": 0, "resync": false, "signals": requests, "sequence": sequence}


func tick() -> int:
	if not _has_program:
		return 0
	if not _owns_player():
		error = "Preview player was replaced externally."
		return ERR_BUSY
	if _target + 0.000000001 < _time:
		var result: int = _player.call("play_package", _package, -1.0)
		if result != 0:
			error = "Could not restart preview package (result %d)." % result
			return result
		_generation = int(_player.call("get_playback_generation"))
		_player.set_process(false)
		_time = 0.0
		_finished = false
		result = _advance(0.0)
		if result != 0:
			return result
	var start_usec := Time.get_ticks_usec()
	for step_index in range(STEPS_PER_FRAME):
		if _finished or _time + 0.000000001 >= _target:
			break
		if step_index > 0 and (Time.get_ticks_usec() - start_usec) >= MAX_CATCHUP_MS * 1000.0:
			break
		var delta: float = minf(STEP, _target - _time)
		var step_result := _advance(delta)
		if step_result != 0:
			_seeking = false
			error = "Package evaluation failed (result %d): %s" % [step_result, str(_player.call("get_gpu_error"))]
			return step_result
		_time += delta
	_seeking = not _finished and (_time + 0.000000001 < _target)
	return 0


func _advance(delta: float) -> int:
	var result: int = _player.call("advance", delta)
	if result == 0 and _player.call("is_playing") and not _owns_player():
		error = "Preview player was replaced during evaluation."
		return ERR_BUSY
	if result == 0 and not _player.call("is_playing"):
		# Natural completion increments the native generation.
		_generation = int(_player.call("get_playback_generation"))
		_finished = true
	return result
