Polymorphic math and type errors
================================

The palette collapses declared operator families. The first non-scalar source
determines the domain; removing it re-infers the domain from remaining wires.
Only declared scalar broadcasts are implicit. Mismatched wires remain editable
and are reported on their target nodes and in the Problems window. Numeric
compilation rejects them. Existing explicitly typed nodes retain their Kind and
behavior; Quaternion is not treated as Vector4.

Math.json schema:
- schemaVersion: currently 1.
- scalarType: the scalar domain name.
- broadcastConversions: target type to existing scalar conversion Kind.
- families: each family specifies baseKind, variants (type to existing Kind),
  domainInputs (participating input IDs), and broadcastInputs (IDs where scalar
  broadcast is allowed).
- fallbackType: domain when no non-scalar source exists; defaults to scalarType.
- outputType: fixed output type when different from the selected input domain.
- displayNameKey: optional localized catalog name.
- defaultMigration: resizeVector permits existing default lanes to be retained
  when the selected vector variant changes; added lanes use the new variant's
  declared defaults. This does not convert connected vector types.

The registry validates its Core kinds, port layouts, categories, domains and
conversions at load. Project format 9 and group format 3 persist the base Kind
and IsPolymorphic marker; the resolved domain is derived from the connections.
Implicit scalar broadcasts lower to hidden existing conversion instructions.
MCP family base kinds create polymorphic nodes by default; isPolymorphic=false
requests an explicitly typed legacy node.

For managed tests, run:
  dotnet run --project .\Tests\CommentImageSmokeTests.csproj -- --graph-type-diagnostics
Set LIBERAFX_NATIVE_TEST_LIBRARY to the absolute path of a built interop library
to additionally verify actual native output lanes. Without it only those native
value checks are skipped.

多相ノードは最初の非スカラー入力から型を決定し、接続解除時に再推論します。
不一致の接続は削除せず、赤枠・ツールチップ・問題一覧に表示します。
スカラーの暗黙複製は宣言された入力だけに適用し、異なるベクトル次元や
Quaternion は自動変換しません。既存の型別ノードは従来どおり保持します。
