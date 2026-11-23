"""Generate the public, bilingual reference for built-in authoring nodes."""

import argparse
import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "Assets"
INTERNAL_PREFIXES = ("MatrixRuntime", "LifecycleRuntime", "GameIoRuntime")
INTERNAL_KINDS = {"RuntimeEffectAge", "RealEffectAge", "RealInstanceAge", "RealDeltaTime"}

OPERATIONS = {
    "Abs": ("Absolute value.", "絶対値を求めます。"),
    "Add": ("Add A and B.", "A と B を加算します。"),
    "Subtract": ("Subtract B from A.", "A から B を引きます。"),
    "Multiply": ("Multiply A by B.", "A と B を乗算します。"),
    "Divide": ("Divide A by B.", "A を B で割ります。"),
    "Max": ("Return the larger value.", "大きい方の値を返します。"),
    "Min": ("Return the smaller value.", "小さい方の値を返します。"),
    "Lerp": ("Linearly interpolate from A to B using the weight input.", "重み入力で A から B へ線形補間します。"),
    "ATan": ("Inverse tangent in radians.", "逆正接をラジアンで求めます。"),
    "ATan2": ("Angle of the supplied Y and X coordinates in radians.", "Y と X 座標が表す角度をラジアンで求めます。"),
    "Clamp": ("Limit a value to the supplied range.", "値を指定範囲に収めます。"),
    "Cos": ("Cosine of an angle in radians.", "ラジアンで指定した角度の余弦を求めます。"),
    "DegToRad": ("Convert degrees to radians.", "度をラジアンに変換します。"),
    "FloatToInt32": ("Explicitly convert a floating-point value to a signed 32-bit integer.", "浮動小数点数を符号付き 32 ビット整数に明示変換します。"),
    "Int32ToFloat": ("Explicitly convert a signed 32-bit integer to a floating-point value.", "符号付き 32 ビット整数を浮動小数点数に明示変換します。"),
    "Floor": ("Round toward negative infinity.", "負の無限大の方向へ丸めます。"),
    "Log": ("Natural logarithm.", "自然対数を求めます。"),
    "Log10": ("Base-10 logarithm.", "常用対数を求めます。"),
    "Mad": ("Multiply, then add: A × B + C.", "A × B + C を求めます。"),
    "NormalizeDeg": ("Wrap an angle expressed in degrees.", "度で表した角度を一定範囲に折り返します。"),
    "NormalizeRad": ("Wrap an angle expressed in radians.", "ラジアンで表した角度を一定範囲に折り返します。"),
    "PI": ("Output π.", "円周率 π を出力します。"),
    "TwoPI": ("Output 2π.", "2π を出力します。"),
    "Pow": ("Raise a base to the supplied exponent.", "底を指定の指数でべき乗します。"),
    "RadToDeg": ("Convert radians to degrees.", "ラジアンを度に変換します。"),
    "Round": ("Round to the nearest integer value.", "最も近い整数値に丸めます。"),
    "Sign": ("Return the sign of a value.", "値の符号を求めます。"),
    "Sin": ("Sine of an angle in radians.", "ラジアンで指定した角度の正弦を求めます。"),
    "Sqrt": ("Square root.", "平方根を求めます。"),
    "Tan": ("Tangent of an angle in radians.", "ラジアンで指定した角度の正接を求めます。"),
    "Time": ("Read time in the current evaluation scope.", "現在の実行スコープの時刻を読み取ります。"),
    "Dot": ("Return the dot product as a scalar.", "内積をスカラーで返します。"),
    "Cross": ("Return the cross product of two 3D vectors.", "二つの 3 次元ベクトルの外積を返します。"),
    "Length": ("Return the Euclidean length as a scalar.", "ユークリッド長をスカラーで返します。"),
    "Normalize": ("Normalize the vector's direction.", "ベクトルの方向を正規化します。"),
    "Break": ("Expose the vector components on separate output pins.", "ベクトルの各成分を個別の出力ピンに分けます。"),
    "Mask": ("Select components using the node's component settings.", "ノードの成分設定で使用する成分を選びます。"),
}

NOTES = {
    "BoolToFloat": ("Convert false to 0 and true to 1 for numeric inputs.", "false を 0、true を 1 に変換し、数値入力へ渡します。"),
    "CpuParticleMeshPosition": ("Sample a mesh surface position, weighted by triangle area. Connect a Procedural Model to the CPU Particle's Emitter Mesh input.", "三角形の面積に応じてメッシュ表面の位置を選びます。CPU Particle の Emitter Mesh 入力へ Procedural Model を接続してください。"),
    "CpuParticleMeshNormal": ("Read the interpolated source-space normal at the same sampled surface point as Mesh Surface Position.", "メッシュ表面の位置と同じサンプリング地点で、元のメッシュ座標系の補間済み法線を読み取ります。"),
    "CpuParticleMeshUV": ("Read interpolated UV coordinates at the same sampled mesh surface point.", "同じメッシュ表面のサンプリング地点で、補間済みの UV 座標を読み取ります。"),
    "CpuParticleMeshColor": ("Read the interpolated linear vertex color at the same sampled mesh surface point. This does not sample a texture.", "同じメッシュ表面のサンプリング地点で、線形空間の頂点色を補間して読み取ります。テクスチャーの色は読み取りません。"),
    "ColorToVector4": ("Represent the RGBA lanes as a Vector4.", "RGBA の各成分を Vector4 として扱います。"),
    "Vector4ToColor": ("Represent the four lanes as RGBA.", "4 成分を RGBA として扱います。"),
    "ComposeVector4": ("Assemble four scalar inputs into a Vector4.", "4 個のスカラー入力を Vector4 に組み立てます。"),
    "Comment": ("Annotate the graph without contributing to runtime execution.", "実行内容を変えずにグラフへ説明を付けます。"),
    "GraphGroup": ("Organize graph contents and expose a group boundary.", "グラフをまとめ、入出力の境界を公開します。"),
    "SpawnableGroup": ("Define a child effect template instantiated by a Spawn signal.", "Spawn シグナルでインスタンス化する子エフェクトのテンプレートを定義します。"),
    "FloatFCurve": ("Sample an editable curve to produce a numeric value.", "編集したカーブをサンプリングして数値を出力します。"),
    "ModelDraw": ("Submit a model draw using resources bound in the host engine.", "ホストエンジンで割り当てたリソースを使ってモデル描画を要求します。"),
    "SpriteDraw": ("Submit a sprite draw using host-engine resources.", "ホストエンジンのリソースを使ってスプライト描画を要求します。"),
    "SpriteAnimation": ("Select a frame from an authored sprite animation.", "スプライトアニメーションからフレームを選びます。"),
    "SpriteComposition": ("Use an authored multi-part sprite composition.", "複数パーツからなるスプライト構成を使います。"),
    "GpuParticle": ("Author GPU particle work for a supported backend; consult the engine compatibility guide.", "対応バックエンド向けの GPU パーティクルを構成します。エンジンの対応範囲を確認してください。"),
    "EffectAge": ("Read elapsed time for the whole effect, with separate scaled-time and real-time outputs.", "エフェクト全体の経過時間を、時間倍率適用後と実時間の二つの出力で読み取ります。"),
    "DeltaTime": ("Read the current evaluation's time step.", "現在の評価で進んだ時間を読み取ります。"),
    "InstanceAge": ("Read elapsed time for the current spawned instance, with separate scaled-time and real-time outputs.", "現在の生成済みインスタンスの経過時間を、時間倍率適用後と実時間の二つの出力で読み取ります。"),
    "InstanceRemaining": ("Read the current instance's remaining lifetime.", "現在のインスタンスの残り寿命時間を読み取ります。"),
    "InstanceNormalizedAge": ("Read the current instance's age relative to its lifetime.", "現在のインスタンスの寿命に対する経過割合を読み取ります。"),
    "LifeTime": ("Set the current scope's lifetime deadline.", "現在のスコープの寿命の期限を設定します。"),
    "OnStart": ("Emit the start signal for the current scope.", "現在のスコープの開始シグナルを出します。"),
    "Signal": ("Author a signal entry used by the graph and its timeline.", "グラフとタイムラインで使うシグナルの入口を作ります。"),
    "OwnerMatrix": ("Read the owner transform supplied by the host.", "ホストから渡されたオーナーの変換を読み取ります。"),
    "CurrentCamera": ("Read the host camera inputs and check their validity.", "ホストのカメラ入力を読み取り、有効かどうかを確認します。"),
    "DecomposeTransform": ("Read position, rotation and scale from a transform matrix.", "変換行列から位置・回転・スケールを取り出します。"),
    "MatrixBreak": ("Expose matrix rows individually.", "行列の各行を個別に取り出します。"),
    "MatrixCompose": ("Assemble a matrix from its rows.", "各行から行列を組み立てます。"),
    "MatrixInverse": ("Compute a matrix inverse and check whether it is valid.", "逆行列を求め、有効かどうかを確認します。"),
    "MatrixMultiply": ("Multiply two matrices in input order.", "入力順に二つの行列を掛け合わせます。"),
    "RotatePoint": ("Rotate a point about the origin.", "点を原点の周りに回転します。"),
    "TranslatePoint": ("Offset a point by a translation.", "点に平行移動を加えます。"),
    "Transform": ("Build a matrix from position, rotation and scale.", "位置・回転・スケールから行列を作ります。"),
    "TransformPoint": ("Transform a point, including translation.", "平行移動を含めて点を変換します。"),
    "TransformDirection": ("Transform a direction without translation.", "平行移動を除いて方向を変換します。"),
}


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def translations(language):
    return {key: value for path in sorted((ASSETS / "Translations").glob(f"*.{language}.json"))
            for key, value in load(path).items()}


def label(definition, strings):
    return strings.get(definition.get("displayNameKey"), definition["name"])


def note(node, strings, language):
    if node.get("descriptionKey") in strings:
        return strings[node["descriptionKey"]]
    kind, group = node["kind"], node["group"]
    op = OPERATIONS.get(kind) or NOTES.get(kind)
    if not op:
        if match := re.fullmatch(r"(.*)Vector[234]", kind):
            op = OPERATIONS.get(match[1])
            if match[1] in {"Abs", "Add", "Subtract", "Multiply", "Divide", "Min", "Max", "Lerp"}:
                op = (op[0] + " Operates component by component.", op[1] + " 成分ごとに計算します。")
        if kind.startswith("Int32") and kind[5:] in OPERATIONS:
            op = OPERATIONS[kind[5:]]
        if kind.startswith("ScalarToVector"):
            op = ("Copy a scalar into the vector's components.", "スカラーの値をベクトルの各成分にコピーします。")
        if re.fullmatch(r"Vector[234]ToVector[234]", kind):
            op = ("Explicitly convert between vector widths; see the input pins for any added components.",
                  "ベクトルの成分数を明示変換します。追加する成分がある場合は入力ピンを確認してください。")
    if op:
        return op[language == "ja"]
    if group == "Parameter nodes":
        return ("設定した値をグラフで使います。" if language == "ja" else "Supply a value configured in the inspector.")
    if group in {"Group boundary", "Spawnable Group", "Game I/O", "Instance Game I/O"}:
        scope = {
            "Group boundary": ("the containing Group", "所属する Group"),
            "Spawnable Group": ("the Spawnable template", "Spawnable テンプレート"),
            "Game I/O": ("the effect root and host", "エフェクトのルートとホスト"),
            "Instance Game I/O": ("a spawned instance and host", "生成済みインスタンスとホスト"),
        }[group]
        direction = "input" if "Input" in kind else "output"
        channel = "signal" if kind.endswith("Signal") else "value"
        if language == "ja":
            caption = {"input": "入力", "output": "出力"}[direction]
            return f"{scope[1]}の境界に{'シグナル' if channel == 'signal' else '値'}の{caption}を作ります。"
        return f"Define a {channel} {direction} at the boundary of {scope[0]}."
    raise ValueError(f"No {language} explanation for {kind}")


def code(value):
    return f"`{str(value).replace('`', '’')}`"


def ports(node, field, strings, japanese):
    declared = node.get(field)
    direction = "input" if field == "inputPorts" else "output"
    dynamic = [group for group in node.get("dynamicPins", []) if group.get("direction", "input") == direction]
    if node["kind"] == "Comment" or (declared == [] and not dynamic):
        return "なし" if japanese else "None"
    entries = []
    if declared is None:
        if not dynamic:
            return ("定義上の既定ピン" if japanese else "Implicit definition port") + f": {code(node['portType'])}"
    else:
        entries.extend(
            f"{label(port, strings)} {code(port['id'])}: {code(port.get('portType', node['portType']))}"
            for port in declared
        )
    for group in dynamic:
        min_count = group.get("minCount", 0)
        max_count = group.get("maxCount", 64)
        default_count = group.get("defaultCount", min_count)
        prefix = group["idPrefix"]
        template = group["nameTemplate"]
        name_range = template.replace("{0}", f"0–{max_count - 1}")
        id_range = f"{prefix}0..{max_count - 1}"
        port_type = group.get("portType", node["portType"])
        if japanese:
            entries.append(f"{name_range} {code(id_range)}: {code(port_type)}（動的 {min_count}〜{max_count}、既定 {default_count}）")
        else:
            entries.append(f"{name_range} {code(id_range)}: {code(port_type)} (dynamic {min_count}–{max_count}, default {default_count})")
    return ", ".join(entries)


def parameters(node, strings, enums, japanese):
    items = []
    for parameter in node.get("parameters", []):
        if parameter.get("hidden", False):
            continue
        kind = parameter["type"]
        default = parameter["defaultValue"]
        enum = enums.get(kind[5:]) if kind.startswith("enum:") else None
        if enum:
            kind = enum["name"]
            options = {value["value"]: label(value, strings) for value in enum["values"]}
            default_text = f"{options[default]} ({default})" if default in options else code(default)
            choices = " / ".join(options.values())
        else:
            default_text = code(json.dumps(default, ensure_ascii=False))
            choices = None
        line = f"- {label(parameter, strings)} {code(parameter['id'])} ({code(kind)}): {default_text}"
        if choices:
            line += f" — {'選択肢' if japanese else 'Options'}: {choices}"
        items.append(line)
    return items


# Numeric family anchors are already public; new families use stable textual IDs.
LEGACY_FAMILIES = (
    "Curve nodes", "Debug (Preview Only)", "GPU", "Game I/O", "Graph nodes",
    "Group boundary", "Instance Game I/O", "Lifecycle", "Logic", "Math", "Matrices",
    "Matrices / Transforms", "Orientation", "Parameter nodes", "Rendering",
    "Signal nodes", "Spawnable Group", "Utilities", "Vectors",
)

CATEGORIES = [
    ("control", "Flow and lifecycle", "実行フローと寿命",
     ("Signal nodes", "Lifecycle", "Spawnable Group", "Logic")),
    ("values", "Values and math", "値と演算",
     ("Parameter nodes", "Math", "Vectors", "Matrices", "Orientation", "Quaternion", "Random", "Curve nodes")),
    ("composition", "Groups and host I/O", "Group とホスト入出力",
     ("Graph nodes", "Group boundary", "Game I/O", "Instance Game I/O", "Matrices / Transforms")),
    ("visuals", "Rendering and particles", "描画とパーティクル",
     ("Rendering", "Flexible Plane", "Emitters", "CPU Particles", "GPU")),
    ("tools", "Authoring and preview", "オーサリングとプレビュー", ("Utilities", "Debug (Preview Only)")),
]


def subfamily(group, kind):
    if group == "Math":
        if kind.startswith("Int32") or kind == "FloatToInt32":
            return "integers", "Integers and conversions", "整数と型変換"
        if kind in {"ATan", "ATan2", "Cos", "DegToRad", "NormalizeDeg", "NormalizeRad",
                    "PI", "RadToDeg", "Sin", "Tan", "TwoPI"}:
            return "angles", "Angles and trigonometry", "角度と三角関数"
        if kind == "Time":
            return "time", "Time", "時間"
        return "scalars", "Scalar operations", "スカラー演算"
    if group == "Vectors":
        if kind.startswith(("Break", "Compose", "Mask")) or "To" in kind:
            return "components", "Components and conversions", "成分と型変換"
        if kind.startswith(("Cross", "Dot", "Length", "Normalize")):
            return "geometry", "Geometry", "幾何演算"
        return "arithmetic", "Vector arithmetic", "ベクトル演算"
    if group == "Spawnable Group":
        if kind.startswith("SpawnInput"):
            return "inputs", "Spawnable inputs", "Spawnable の入力"
        if kind.startswith("SpawnOutput"):
            return "outputs", "Spawnable outputs", "Spawnable の出力"
        return "scope", "Instance events and age", "インスタンスのイベントと経過時間"
    return None


def render(nodes, enums, modules, language):
    japanese = language == "ja"
    strings = translations(language)
    groups = {}
    for node in nodes:
        groups.setdefault(node["group"], []).append(node)
    text = [
        "---", f"title: {'標準ノード図鑑' if japanese else 'Built-in Node Reference'}", "---", "",
        "[トップ](index.md) · [画面リファレンス](editor-reference.md) · [グラフ制作ガイド](authoring.md)"
        if japanese else
        "[Home](index.md) · [Editor reference](editor-reference.md) · [Graph authoring](authoring.md)",
        "", "# 標準ノード図鑑" if japanese else "# Built-in Node Reference", "",
        "ピン・型・既定値の意味は[グラフの用語](glossary.md#graphs)を参照してください。"
        if japanese else
        "See the [graph glossary](glossary.md#graphs) for pins, types, and defaults.", "",
        "追加された10種類の演算と使用例は[スカラー演算の使い方](scalar-math-nodes.md)を参照してください。更新後のランタイム・SDK が必要です。"
        if japanese else
        "See [scalar math recipes](scalar-math-nodes.md) for the ten new operations and examples. These require the updated runtime/SDK.", "",
        "[CPU パーティクル](cpu-particles.md)と[Flexible Plane Draw](flexible-plane-draw.md)には、専用の制作手順と対応範囲の説明があります。"
        if japanese else
        "See [CPU particles](cpu-particles.md) and [Flexible Plane Draw](flexible-plane-draw.md) for their dedicated workflows and support boundaries.", "",
        "[クォータニオンと乱数](quaternion-random.md)では、回転の表現と再現可能な乱数の使い方を説明します。"
        if japanese else
        "See [quaternions and random values](quaternion-random.md) for rotation conventions and reproducible sampling.", "",
        f"標準ノードの {len(nodes)} 定義を掲載しています。一部の型別定義は、独立した検索項目ではなく、接続に応じて切り替わります。ピンと既定値は基本定義のものです。掲載は全エンジンでの再生を保証するものではありません。"
        if japanese else
        f"This reference covers {len(nodes)} built-in authoring definitions. Some typed variants are selected by connections rather than listed separately in search. Pins and defaults come from the base definitions. Inclusion does not guarantee playback on every engine.",
        "",
    ]
    families = {}
    for group, entries in sorted(groups.items()):
        title = next((strings[n["groupNameKey"]] for n in entries if n.get("groupNameKey") in strings), group)
        if japanese:
            title = {"Graph nodes": "グラフ", "Utilities": "ユーティリティ", "Curve nodes": "カーブ",
                     "Parameter nodes": "パラメーター", "Signal nodes": "シグナル",
                     "Debug (Preview Only)": "デバッグ（プレビュー専用）",
                     "Group boundary": "Group の入出力"}.get(title, title)
        if group == "Matrices / Transforms":
            title = "ホストから受け取る変換" if japanese else "Host transforms"
        buckets = {}
        for node in sorted(entries, key=lambda n: n["kind"].casefold()):
            buckets.setdefault(subfamily(group, node["kind"]), []).append(node)
        family_id = (LEGACY_FAMILIES.index(group) if group in LEGACY_FAMILIES else
                     "group-" + re.sub(r"[^a-z0-9]+", "-", group.casefold()).strip("-"))
        families[group] = (family_id, title, buckets)
    categories = list(CATEGORIES)
    categorized = {group for _, _, _, group_names in categories for group in group_names}
    uncategorized = tuple(group for group in families if group not in categorized)
    if uncategorized:
        categories.append(("other", "Other definitions", "その他の定義", uncategorized))
    for category, en, ja, group_names in categories:
        text.append(f"- [{ja if japanese else en}](#category-{category})")
        for group in group_names:
            if group not in families:
                continue
            index, title, buckets = families[group]
            text.append(f"  - [{title} ({sum(map(len, buckets.values()))})](#family-{index})")
            for bucket, entries in buckets.items():
                if bucket:
                    slug, bucket_en, bucket_ja = bucket
                    text.append(f"    - [{bucket_ja if japanese else bucket_en} ({len(entries)})](#family-{index}-{slug})")
    module_title = "標準 Group モジュール" if japanese else "Standard Group Modules"
    text.append(f"- [{module_title} ({len(modules)})](#standard-modules)")
    for category, en, ja, group_names in categories:
        text += ["", f'<span id="category-{category}"></span>', f"## {ja if japanese else en}", ""]
        for group in group_names:
            if group not in families:
                continue
            index, title, buckets = families[group]
            text += [f'<span id="family-{index}"></span>', f"### {title}", ""]
            for bucket, entries in buckets.items():
                if bucket:
                    slug, bucket_en, bucket_ja = bucket
                    text += [f'<span id="family-{index}-{slug}"></span>', f"#### {bucket_ja if japanese else bucket_en}", ""]
                heading = "#####" if bucket else "####"
                for node in entries:
                    identifier = node["kind"]
                    text += [
                        f'<span id="node-{identifier.lower()}"></span>', f"{heading} {label(node, strings)}", "",
                        f"`{identifier}` — {note(node, strings, language)}", "",
                        f"- **{'入力' if japanese else 'Inputs'}:** {ports(node, 'inputPorts', strings, japanese)}",
                        f"- **{'出力' if japanese else 'Outputs'}:** {ports(node, 'outputPorts', strings, japanese)}",
                    ]
                    if items := parameters(node, strings, enums, japanese):
                        text += [f"- **{'設定と既定値' if japanese else 'Settings and defaults'}:**"] + [
                            f"  {item}" for item in items]
                    text.append("")
    text += ["", '<span id="standard-modules"></span>', f"## {module_title}", "",
             "同梱モジュールはロック済みの再利用 Group です。中のグラフを変更する場合は、プロジェクトライブラリへ複製してください。"
             if japanese else
             "These bundled modules are locked reusable groups. Clone one to your project library to edit its internal graph.", ""]
    fields = [
        ("inputsKey", "Inputs", "入力"), ("outputsKey", "Outputs", "出力"),
        ("phaseKey", "Phase", "フェーズ"), ("unitsKey", "Units", "単位"),
        ("stateKey", "State", "状態"), ("backendKey", "Backend", "バックエンド"),
    ]
    for module in modules:
        text += [f"### {strings[module['nameKey']]}", "", strings[module["descriptionKey"]], ""]
        for key, english, ja in fields:
            text.append(f"- **{ja if japanese else english}:** {strings[module[key]]}")
        for parameter in module["parameters"]:
            text.append(f"- **{strings[parameter['nameKey']]}:** {strings[parameter['descriptionKey']]}")
        text.append("")
    return "\n".join(text)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("site", type=Path, help="Staged Jekyll site directory")
    target = parser.parse_args().site
    nodes = [load(path) for path in sorted((ASSETS / "Nodes").glob("*.json"))]
    nodes = [node for node in nodes if not (node["namespace"] == "LiberaFx" and
                                           (node["kind"].startswith(INTERNAL_PREFIXES) or node["kind"] in INTERNAL_KINDS))]
    assert nodes and len(nodes) == len({node["kind"] for node in nodes})
    enums = {enum["typeId"]: enum for path in sorted((ASSETS / "Enums").glob("*.json")) if (enum := load(path))}
    modules = [module for module in load(ASSETS / "Modules" / "manifest.json") if not module.get("legacy")]
    for language, directory in (("en", target), ("ja", target / "ja")):
        directory.mkdir(parents=True, exist_ok=True)
        (directory / "node-reference.md").write_text(render(nodes, enums, modules, language), encoding="utf-8")
    print(f"Generated {len(nodes)} built-in node entries and {len(modules)} standard modules in English and Japanese.")


if __name__ == "__main__":
    main()
