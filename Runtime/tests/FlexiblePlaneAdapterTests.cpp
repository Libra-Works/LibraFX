#include "liberafx/host/LbFlexiblePlaneAdapter.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>

int main()
{
    using namespace liberafx::flexible_adapter;
    auto definition = Definition{};
    definition.source[15] = 1;
    definition.history = true;
    definition.capacity = 3;
    definition.maxAge = 10;
    definition.options = {0, 0, 0, 1, 0, 0, 0, 0, 1};
    definition.inputs = {
        {"width", {}, {1, 0, 0, 0}, 1, 0, false},
        {"tint", {}, {1, 1, 1, 1}, 4, 0, false},
        {"position", {}, {0, 0, 0, 0}, 3, 0, false},
        {"pointWidth", {}, {1, 0, 0, 0}, 1, 0, false},
        {"pointColor", {}, {1, 1, 1, 1}, 4, 0, false}
    };
    assert(validate(definition));
    auto scope = HistoryScope{};
    auto geometry = Geometry(definition);
    auto other = Geometry(definition);
    auto noRead = Read{};
    scope.beginUpdate();
    assert(geometry.update(definition, scope, noRead, 0, 0, 0, 1) == LIBRA_OK);
    assert(geometry.counts().vertices == 0);
    assert(other.update(definition, scope, noRead, 0, 0, 0, 1) == LIBRA_OK);
    definition.inputs[2].defaults[0] = 1;
    assert(other.update(definition, scope, noRead, 0, 0, 0, 1) == LIBRA_INVALID_BINARY);
    definition.inputs[2].defaults[0] = 0;
    definition.inputs[2].connected = true;
    definition.inputs[2].source[15] = 2;
    scope.reset();
    auto position = 0.0;
    auto read = Read{[&position](const Id&, double* output, std::uint8_t& width) -> LibraResult {
        output[0] = position;
        output[1] = 0;
        output[2] = 0;
        width = 3;
        return LIBRA_OK;
    }};
    scope.beginUpdate();
    assert(geometry.update(definition, scope, read, 0, 0, 0, 1) == LIBRA_OK);
    position = 1;
    scope.beginUpdate();
    assert(geometry.update(definition, scope, read, 1, 0, 0, 1) == LIBRA_OK);
    assert(geometry.counts().vertices == 4 && geometry.counts().indices == 6);
    position = 2;
    assert(other.update(definition, scope, read, 1, 0, 0, 1) == LIBRA_INVALID_ARGUMENT);
    scope.beginUpdate();
    assert(geometry.update(definition, scope, read, 0, 0, 0, 1) == LIBRA_OK);
    assert(geometry.counts().vertices == 0);
}
