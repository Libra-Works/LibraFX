#include "liberafx/InteropSession.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <array>
#include <atomic>
#include <cassert>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <thread>

namespace {
auto program(double value, std::uint8_t opcode = 1) -> std::array<std::uint8_t, 56>
{
    auto bytes = std::array<std::uint8_t, 56>{};
    std::memcpy(bytes.data(), "LFXB", 4);
    bytes[4] = bytes[6] = bytes[8] = 1;
    bytes[27] = 1; bytes[28] = opcode;
    for (auto index = 32; index < 40; ++index) bytes[index] = 0xff;
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, 8);
    for (auto index = 0u; index < 8; ++index) bytes[40 + index] = static_cast<std::uint8_t>(bits >> (index * 8));
    return bytes;
}
auto create(std::uint32_t slots = 2, std::uint32_t requests = 2) -> LibraSessionId
{
    auto options = LibraSessionOptions{sizeof(LibraSessionOptions), slots, requests,
        LIBRA_SESSION_DEVELOPMENT_OPT_IN, 100, 200, 1024 * 1024, 8 * 1024 * 1024};
    auto session = LibraSessionId{};
    assert(libra_session_create(&options, &session) == LIBRA_OK && session.incarnation);
    return session;
}
auto take(LibraSessionId session, LibraSessionRequestId request) -> LibraSessionCompletion
{
    auto processed = std::uint32_t{};
    assert(libra_session_process(session, 1, &processed) == LIBRA_OK && processed == 1);
    auto completion = LibraSessionCompletion{};
    assert(libra_session_take_completion(session, request, &completion) == LIBRA_OK);
    auto replay = LibraSessionCompletion{};
    assert(libra_session_take_completion(session, request, &replay) == LIBRA_INVALID_HANDLE);
    return completion;
}
auto control(LibraSessionId session, std::uint32_t kind, LibraSessionInstanceId instance) -> LibraSessionCompletion
{
    auto request = LibraSessionRequestId{};
    assert(libra_session_submit_control(session, kind, instance, &request) == LIBRA_OK);
    return take(session, request);
}
void transactions()
{
    assert(libra_session_abi_version() == 1);
    auto options = LibraSessionOptions{sizeof(LibraSessionOptions), 2, 2, 0, 100, 200, 1024, 1024 * 1024};
    auto invalid = LibraSessionId{};
    assert(libra_session_create(&options, &invalid) == LIBRA_INVALID_ARGUMENT && !invalid.incarnation);
    auto session = create();
    auto foreignSession = create(); // Even identical nonces have distinct incarnations.
    auto first = program(3), next = program(9), failed = program(9, 6);
    auto request = LibraSessionRequestId{};
    auto producer = std::thread([&]() {
        assert(libra_session_submit_program(session, LIBRA_SESSION_CREATE, {}, first.data(), first.size(),
            2, 0, LIBRA_SESSION_PRESERVE_STATE, &request) == LIBRA_OK);
        auto processed = std::uint32_t{99};
        assert(libra_session_process(session, 1, &processed) == LIBRA_INVALID_ARGUMENT && processed == 0);
    });
    producer.join();
    first = program(100); // Submitted bytes are COPIED, not retained.
    auto completion = LibraSessionCompletion{};
    assert(libra_session_take_completion(session, request, &completion) == LIBRA_NOT_READY);
    assert(libra_session_take_completion(foreignSession, request, &completion) == LIBRA_INVALID_HANDLE);
    auto processed = std::uint32_t{};
    assert(libra_session_process(session, 1, &processed) == LIBRA_OK && processed == 1);
    auto consumer = std::thread([&]() {
        assert(libra_session_take_completion(session, request, &completion) == LIBRA_OK &&
            completion.result == LIBRA_OK);
    });
    consumer.join();
    auto instance = completion.instance;
    auto value = LibraValue{};
    auto read = [&]() {
        assert(libra_session_read_value(session, instance, first.data() + 12, &value) == LIBRA_OK);
        return value.x;
    };
    assert(read() == 3 && value.componentCount == 1);
    assert(libra_session_read_value(foreignSession, instance, first.data() + 12, &value) == LIBRA_INVALID_HANDLE);
    auto finished = std::uint32_t{};
    assert(libra_session_advance(session, instance, 1, 1, &finished) == LIBRA_OK && !finished);
    assert(libra_session_submit_program(session, LIBRA_SESSION_REPLACE, instance, next.data(), next.size(),
        2, 0, LIBRA_SESSION_PRESERVE_STATE, &request) == LIBRA_OK);
    assert(take(session, request).result == LIBRA_UNSUPPORTED_BINARY && read() == 3);
    assert(libra_session_submit_program(session, LIBRA_SESSION_REPLACE, instance, failed.data(), failed.size(),
        2, 0, LIBRA_SESSION_RESTART, &request) == LIBRA_OK);
    assert(take(session, request).result == LIBRA_DIVIDE_BY_ZERO && read() == 3);
    assert(libra_session_advance(session, instance, 0.5, 0.5, &finished) == LIBRA_INVALID_ARGUMENT);
    assert(libra_session_submit_program(session, LIBRA_SESSION_REPLACE, instance, next.data(), next.size(),
        2, 0, LIBRA_SESSION_RESTART, &request) == LIBRA_OK);
    assert(take(session, request).result == LIBRA_OK && read() == 9);
    assert(libra_session_advance(session, instance, 1, 1, &finished) == LIBRA_OK && !finished);
    assert(libra_session_advance(session, instance, 2, 1, &finished) == LIBRA_OK && finished);
    assert(read() == 9); // Natural expiry keeps the ID and native state alive.
    assert(libra_session_release(session) == LIBRA_NOT_READY);
    assert(control(session, LIBRA_SESSION_DESTROY, instance).result == LIBRA_OK);
    assert(libra_session_read_value(session, instance, first.data() + 12, &value) == LIBRA_INVALID_HANDLE);
    assert(libra_session_release(session) == LIBRA_OK);
    assert(libra_session_process(session, 1, &processed) == LIBRA_INVALID_HANDLE);
    assert(libra_session_release(foreignSession) == LIBRA_OK);
}
void creditsAndGenerations()
{
    auto session = create(1, 1);
    auto bytes = program(4);
    auto request = LibraSessionRequestId{}, rejected = LibraSessionRequestId{};
    assert(libra_session_submit_program(session, LIBRA_SESSION_CREATE, {}, bytes.data(), bytes.size(),
        0, 1, LIBRA_SESSION_PRESERVE_STATE, &request) == LIBRA_OK);
    auto processed = std::uint32_t{};
    assert(libra_session_process(session, 1, &processed) == LIBRA_OK && processed == 1);
    assert(libra_session_submit_program(session, LIBRA_SESSION_CREATE, {}, bytes.data(), bytes.size(),
        0, 1, LIBRA_SESSION_PRESERVE_STATE, &rejected) == LIBRA_QUEUE_REJECTED && !rejected.sequence);
    auto completion = LibraSessionCompletion{};
    assert(libra_session_take_completion(session, request, &completion) == LIBRA_OK && completion.result == LIBRA_OK);
    auto old = completion.instance;
    assert(control(session, LIBRA_SESSION_DESTROY, old).result == LIBRA_OK);
    assert(libra_session_submit_program(session, LIBRA_SESSION_CREATE, {}, bytes.data(), bytes.size(),
        0, 1, LIBRA_SESSION_PRESERVE_STATE, &request) == LIBRA_OK);
    auto fresh = take(session, request).instance;
    assert(fresh.slot == old.slot && fresh.generation != old.generation);
    assert(control(session, LIBRA_SESSION_DESTROY, old).result == LIBRA_INVALID_HANDLE);
    assert(control(session, LIBRA_SESSION_NATURAL_END, fresh).result == LIBRA_OK);
    auto finished = std::uint32_t{};
    assert(libra_session_advance(session, fresh, 1, 1, &finished) == LIBRA_OK && finished);
    assert(control(session, LIBRA_SESSION_DESTROY, fresh).result == LIBRA_OK);
    assert(libra_session_release(session) == LIBRA_OK);
}
void churn()
{
    constexpr auto workerCount = std::size_t{4};
    auto session = create(workerCount, 2);
    auto workers = std::array<std::thread, workerCount>{};
    auto done = std::atomic<std::size_t>{0};
    auto wait = [](std::chrono::steady_clock::time_point deadline) {
        assert(std::chrono::steady_clock::now() < deadline);
        std::this_thread::yield();
    };
    for (auto index = std::size_t{}; index < workerCount; ++index) {
        workers[index] = std::thread([&]() {
            auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
            auto bytes = program(7);
            auto consume = [&](LibraSessionRequestId request) {
                auto result = LibraResult{};
                auto completion = LibraSessionCompletion{};
                while ((result = libra_session_take_completion(session, request, &completion)) == LIBRA_NOT_READY)
                    wait(deadline);
                assert(result == LIBRA_OK && completion.result == LIBRA_OK);
                return completion;
            };
            for (auto iteration = 0u; iteration < 32; ++iteration) {
                auto request = LibraSessionRequestId{};
                auto result = LibraResult{};
                while ((result = libra_session_submit_program(session, LIBRA_SESSION_CREATE, {},
                    bytes.data(), bytes.size(), 0, 1, LIBRA_SESSION_PRESERVE_STATE, &request)) == LIBRA_QUEUE_REJECTED)
                    wait(deadline);
                assert(result == LIBRA_OK);
                auto created = consume(request);
                while ((result = libra_session_submit_control(session, LIBRA_SESSION_DESTROY,
                    created.instance, &request)) == LIBRA_QUEUE_REJECTED) wait(deadline);
                assert(result == LIBRA_OK && consume(request).result == LIBRA_OK);
            }
            ++done;
        });
    }
    auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
    while (done.load() != workerCount) {
        auto processed = std::uint32_t{};
        assert(libra_session_process(session, 2, &processed) == LIBRA_OK);
        wait(deadline);
    }
    for (auto& worker : workers) worker.join();
    assert(libra_session_release(session) == LIBRA_OK);
}
}
int main()
{
    transactions();
    creditsAndGenerations();
    churn();
}
