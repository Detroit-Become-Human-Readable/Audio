#include "BnkExtractor.h"
#include <iostream>
#include <cstring>

int Swap32(const uint32_t dword) {
#ifdef __GNUC__
    return __builtin_bswap32(dword);
#elif _MSC_VER
    return _byteswap_ulong(dword);
#endif
}

template <typename T>
bool ReadContent(std::fstream& file, T& structure) {
    return static_cast<bool>(file.read(reinterpret_cast<char*>(&structure), sizeof(structure)));
}

std::filesystem::path CreateOutputDirectory(std::filesystem::path bnk_filename) {
    const auto directory_name = bnk_filename.filename().replace_extension("");
    auto directory = bnk_filename.replace_filename(directory_name);
    std::filesystem::create_directory(directory);
    return directory;
}

bool Compare(char* char_string, const std::string& string) {
    return std::strncmp(char_string, string.c_str(), string.length()) == 0;
}

extern "C" void ExtractBnkFile(const char* bnkFilePath, const char* outputDirectory, bool swapByteOrder, bool dumpObjects) {
    auto bnk_filename = std::filesystem::path{ std::string{ bnkFilePath } };
    auto output_directory = std::filesystem::path{ std::string{ outputDirectory } };

    auto bnk_file = std::fstream{ bnk_filename, std::ios::binary | std::ios::in };

    // Could not open BNK file
    if (!bnk_file.is_open()) {
        std::cerr << "Can't open input file: " << bnk_filename << "\n";
        return;
    }

    auto data_offset = std::size_t{ 0U };
    auto files = std::vector<Index>{};
    auto content_section = Section{};
    auto content_index = Index{};
    auto bank_header = BankHeader{};
    auto objects = std::vector<Object>{};
    auto event_objects = std::map<std::uint32_t, EventObject>{};
    auto event_action_objects = std::map<std::uint32_t, EventActionObject>{};

    while (ReadContent(bnk_file, content_section)) {
        const std::size_t section_pos = bnk_file.tellg();

        if (swapByteOrder) {
            content_section.size = Swap32(content_section.size);
        }

        if (Compare(content_section.sign, "BKHD")) {
            ReadContent(bnk_file, bank_header);
            bnk_file.seekg(content_section.size - sizeof(BankHeader), std::ios_base::cur);

            std::cout << "Wwise Bank Version: " << bank_header.version << "\n";
            std::cout << "Bank ID: " << bank_header.id << "\n";
        }
        else if (Compare(content_section.sign, "DIDX")) {
            // Read file indices
            for (auto i = 0U; i < content_section.size; i += sizeof(content_index)) {
                ReadContent(bnk_file, content_index);
                files.push_back(content_index);
            }
        }
        else if (Compare(content_section.sign, "STID")) {
            // To be implemented
        }
        else if (Compare(content_section.sign, "DATA")) {
            data_offset = bnk_file.tellg();
        }
        else if (Compare(content_section.sign, "HIRC")) {
            auto object_count = std::uint32_t{ 0 };
            ReadContent(bnk_file, object_count);

            for (auto i = 0U; i < object_count; ++i) {
                auto object = Object{};
                ReadContent(bnk_file, object);

                if (object.type == ObjectType::Event) {
                    auto event = EventObject{};

                    if (bank_header.version >= 134) {
                        auto count = std::uint8_t{ 0 };
                        ReadContent(bnk_file, count);
                        event.action_count = static_cast<std::uint32_t>(count);
                    }
                    else {
                        ReadContent(bnk_file, event.action_count);
                    }

                    for (auto j = 0U; j < event.action_count; ++j) {
                        auto action_id = std::uint32_t{ 0 };
                        ReadContent(bnk_file, action_id);
                        event.action_ids.push_back(action_id);
                    }

                    event_objects[object.id] = event;
                }
                else if (object.type == ObjectType::EventAction) {
                    auto event_action = EventActionObject{};

                    ReadContent(bnk_file, event_action.scope);
                    ReadContent(bnk_file, event_action.action_type);
                    ReadContent(bnk_file, event_action.game_object_id);

                    bnk_file.seekg(1, std::ios_base::cur);

                    ReadContent(bnk_file, event_action.parameter_count);

                    for (auto j = 0U; j < static_cast<std::size_t>(event_action.parameter_count); ++j) {
                        auto parameter_type = EventActionParameterType{};
                        ReadContent(bnk_file, parameter_type);
                        event_action.parameters_types.push_back(parameter_type);
                    }

                    for (auto j = 0U; j < static_cast<std::size_t>(event_action.parameter_count); ++j) {
                        auto parameter = std::int8_t{ 0 };
                        ReadContent(bnk_file, parameter);
                        event_action.parameters.push_back(parameter);
                    }

                    bnk_file.seekg(1, std::ios_base::cur);
                    bnk_file.seekg(object.size - 13 - event_action.parameter_count * 2, std::ios_base::cur);

                    event_action_objects[object.id] = event_action;
                }

                bnk_file.seekg(object.size - sizeof(std::uint32_t), std::ios_base::cur);
                objects.push_back(object);
            }
        }

        // Seek to the end of the section
        bnk_file.seekg(section_pos + content_section.size);
    }

    // Reset EOF
    bnk_file.clear();

    if (!outputDirectory) {
        output_directory = CreateOutputDirectory(bnk_filename);
    }

    // Dump objects information
    if (dumpObjects) {
        auto object_filename = output_directory;
        object_filename = object_filename.append("objects.txt");
        auto object_file = std::fstream{ object_filename, std::ios::out | std::ios::binary };

        if (!object_file.is_open()) {
            std::cerr << "Unable to write objects file '" << object_filename.string() << "'\n";
            return;
        }

        for (auto& [type, size, id] : objects) {
            object_file << "Object ID: " << id << "\n";

            switch (type) {
            case ObjectType::Event:
                object_file << "\tType: Event\n";
                object_file << "\tNumber of Actions: " << event_objects[id].action_count << "\n";

                for (auto& action_id : event_objects[id].action_ids) {
                    object_file << "\tAction ID: " << action_id << "\n";
                }
                break;
            case ObjectType::EventAction:
                object_file << "\tType: EventAction\n";
                object_file << "\tAction Scope: " << static_cast<int>(event_action_objects[id].scope) << "\n";
                object_file << "\tAction Type: " << static_cast<int>(event_action_objects[id].action_type) << "\n";
                object_file << "\tGame Object ID: " << static_cast<int>(event_action_objects[id].game_object_id) << "\n";
                object_file << "\tNumber of Parameters: " << static_cast<int>(event_action_objects[id].parameter_count) << "\n";

                for (auto j = 0; j < event_action_objects[id].parameter_count; ++j) {
                    object_file << "\t\tParameter Type: " << static_cast<int>(event_action_objects[id].parameters_types[j]) << "\n";
                    object_file << "\t\tParameter: " << static_cast<int>(event_action_objects[id].parameters[j]) << "\n";
                }
                break;
            default:
                object_file << "\tType: " << static_cast<int>(type) << "\n";
            }
        }

        std::cout << "Objects file was written to: " << object_filename.string() << "\n";
    }

    // Extract WEM files
    if (data_offset == 0U || files.empty()) {
        std::cerr << "No WEM files discovered to be extracted\n";
        return;
    }

    std::cout << "Found " << files.size() << " WEM files\n";
    std::cout << "Start extracting...\n";

    for (auto& [id, offset, size] : files) {
        auto wem_filename = output_directory;
        wem_filename = wem_filename.append(std::to_string(id)).replace_extension(".wem");
        auto wem_file = std::fstream{ wem_filename, std::ios::out | std::ios::binary };

        if (swapByteOrder) {
            size = Swap32(size);
            offset = Swap32(offset);
        }

        if (!wem_file.is_open()) {
            std::cerr << "Unable to write file '" << wem_filename.string() << "'\n";
            continue;
        }

        auto data = std::vector<char>(size, 0U);

        bnk_file.seekg(data_offset + offset);
        bnk_file.read(data.data(), size);
        wem_file.write(data.data(), size);
    }

    std::cout << "Files were extracted to: " << output_directory.string() << "\n";
}
