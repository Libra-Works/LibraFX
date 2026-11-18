// Include inside namespace liberafx, after the instruction decoding helpers.
// These methods capture operands already selected by the evaluator. They must
// never follow graph links recursively or call into descriptors themselves.
auto Instance::debugResolvedInputs(std::uint32_t index, double time, const double* inputs,
    const std::uint8_t* widths, const std::uint8_t* colorFlags,
    const std::uint8_t* signalSource) noexcept -> Result
{
    if (!debugging_.beforeNode) return Result::Ok;
    auto current = instruction(program_->bytes_, program_->version_, index);
    auto values = std::array<DebugInputValue, customMaxPorts>{};
    for (auto slot = 0u; slot < current[18]; ++slot)
    {
        auto& value = values[slot];
        value.slot = static_cast<std::uint8_t>(slot);
        value.width = widths[slot];
        value.kind = colorFlags && colorFlags[slot] ? DebugValueKind::Color : DebugValueKind::Numeric;
        if (program_->version_ >= 10)
        {
            auto type = valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_, index)[2 + slot];
            if (type == valueTypeInt32 || type == valueTypeEnum) value.kind = DebugValueKind::Int32;
            else if (type == valueTypeColor) value.kind = DebugValueKind::Color;
        }
        auto source = read32(current + 48 + slot * 8);
        value.connected = source != noInput;
        if (value.connected)
            std::memcpy(value.sourceNodeId, instruction(program_->bytes_, program_->version_, source), 16);
        for (auto lane = 0u; lane < value.width; ++lane)
            value.components[lane] = inputs[slot * 4 + lane];
    }
    return debugging_.beforeNode(debugging_.context, {current, values.data(), current[18], time, signalSource});
}

auto Instance::debugEnumSelect(std::uint32_t index, std::uint32_t selector,
    std::uint32_t selectedSource, const std::uint8_t* selectedDefaults) noexcept -> Result
{
    if (!debugging_.beforeNode) return Result::Ok;
    auto current = instruction(program_->bytes_, program_->version_, index);
    auto values = std::array<DebugInputValue, 2>{};
    values[0].width = 1;
    values[0].kind = DebugValueKind::Int32;
    values[0].connected = true;
    values[0].components[0] = work_[selector * 4];
    std::memcpy(values[0].sourceNodeId, instruction(program_->bytes_, program_->version_, selector), 16);
    values[1].slot = 1; // Only the selected case; no inactive branch is read.
    values[1].width = current[17];
    values[1].kind = current[20] & 1 ? DebugValueKind::Color : DebugValueKind::Numeric;
    if (program_->version_ >= 10)
    {
        auto type = valueTypeRecord(program_->bytes_, program_->valueTypeMetadataOffset_, index)[0];
        if (type == valueTypeInt32 || type == valueTypeEnum) values[1].kind = DebugValueKind::Int32;
        else if (type == valueTypeColor) values[1].kind = DebugValueKind::Color;
    }
    values[1].connected = selectedSource != noInput;
    if (values[1].connected)
        std::memcpy(values[1].sourceNodeId, instruction(program_->bytes_, program_->version_, selectedSource), 16);
    for (auto lane = 0u; lane < values[1].width; ++lane)
        values[1].components[lane] = values[1].connected ? work_[selectedSource * 4 + lane] :
            readDouble(selectedDefaults + lane * 8);
    return debugging_.beforeNode(debugging_.context, {current, values.data(), values.size(), time_});
}
