#pragma once
#include <string>
#include <vector>
#include <map>
#include <cstdint>
#include <filesystem>
#include <fstream>

extern "C" {
    struct Index {
        std::uint32_t id;
        std::uint32_t offset;
        std::uint32_t size;
    };

    struct Section {
        char sign[4];
        std::uint32_t size;
    };

    struct BankHeader {
        std::uint32_t version;
        std::uint32_t id;
    };

    enum class ObjectType : std::int8_t {
        SoundEffectOrVoice = 2,
        EventAction = 3,
        Event = 4,
        RandomOrSequenceContainer = 5,
        SwitchContainer = 6,
        ActorMixer = 7,
        AudioBus = 8,
        BlendContainer = 9,
        MusicSegment = 10,
        MusicTrack = 11,
        MusicSwitchContainer = 12,
        MusicPlaylistContainer = 13,
        Attenuation = 14,
        DialogueEvent = 15,
        MotionBus = 16,
        MotionFx = 17,
        Effect = 18,
        Unknown = 19,
        AuxiliaryBus = 20
    };

    struct Object {
        ObjectType type;
        std::uint32_t size;
        std::uint32_t id;
    };

    struct EventObject {
        std::uint32_t action_count;
        std::vector<std::uint32_t> action_ids;
    };

    enum class EventActionScope : std::int8_t {
        SwitchOrTrigger = 1,
        Global = 2,
        GameObject = 3,
        State = 4,
        All = 5,
        AllExcept = 6
    };

    enum class EventActionType : std::int8_t {
        Stop = 1,
        Pause = 2,
        Resume = 3,
        Play = 4,
        Trigger = 5,
        Mute = 6,
        UnMute = 7,
        SetVoicePitch = 8,
        ResetVoicePitch = 9,
        SetVoiceVolume = 10,
        ResetVoiceVolume = 11,
        SetBusVolume = 12,
        ResetBusVolume = 13,
        SetVoiceLowPassFilter = 14,
        ResetVoiceLowPassFilter = 15,
        EnableState = 16,
        DisableState = 17,
        SetState = 18,
        SetGameParameter = 19,
        ResetGameParameter = 20,
        SetSwitch = 21,
        ToggleBypass = 22,
        ResetBypassEffect = 23,
        Break = 24,
        Seek = 25
    };

    enum class EventActionParameterType : std::int8_t {
        Delay = 0x0E,
        Play = 0x0F,
        Probability = 0x10
    };

    struct EventActionObject {
        EventActionScope scope;
        EventActionType action_type;
        std::uint32_t game_object_id;
        std::uint8_t parameter_count;
        std::vector<EventActionParameterType> parameters_types;
        std::vector<std::int8_t> parameters;
    };

    void ExtractBnkFile(const char* bnkFilePath, const char* outputDirectory, bool swapByteOrder, bool dumpObjects);
}
