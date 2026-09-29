// ============================================================================
//  VpIpc.h —— 视频展台 启动握手协议（C++ 侧）
//  开发商：UNSA Studio
//
//  ⚠ 该文件中的结构体布局必须与 C# 侧
//    src/VideoPresenter.Shared/Ipc/VpSharedHeader.cs 逐字节一致。
//    任何一方改动字段顺序 / 类型 / 对齐，都必须同步另一端并递增协议版本号。
// ============================================================================
#pragma once

#include <windows.h>
#include <cstdint>

// ── C 运行时（MSVC 安全 CRT）──────────────────────────────────────────────────
//  ⚠ 这两个头必须【在本文件内】包含，不能依赖使用方。
//     本文件在第 73 / 75 行使用了 swprintf_s，而 main.cpp 的
//     #include <cstdio> 出现在 #include "VpIpc.h" 之后 ——
//     编译器是单遍的，轮到 VpIpc.h 时还看不到该声明。
//     （CI 上曾因此在 VpIpc.h:73/75 报 'swprintf_s': identifier not found）
#include <stdio.h>    // swprintf_s（C11 Annex K + MSVC 模板重载）
#include <wchar.h>    // wcscpy_s / wcscat_s / wcsrchr / wcslen

// ── 协议常量 ────────────────────────────────────────────────────────────────
#define VP_MAGIC             0x31535056u   // 'V','P','S','1'
#define VP_PROTOCOL_VERSION  1u
#define VP_MAPPING_SIZE      4096          // 一整页
#define VP_HEADER_SIZE       512           // 定长头部，尾部为通用载荷区
#define VP_MAX_MESSAGE       128

#pragma pack(push, 1)

// 共享内存头部（320 字节），与 C# VpSharedHeader 对齐
struct VP_SHARED_HEADER
{
    uint32_t      magic;         // 'V','P','S','1'
    uint32_t      version;       // 协议版本
    volatile LONG state;         // VP_BOOT_STATE
    uint32_t      launcherPid;   // 启动器 PID
    uint32_t      appPid;        // 主程序 PID（由主程序回填）
    uint64_t      launcherHwnd;  // 启动器 Splash 句柄
    uint64_t      appHwnd;       // 主程序主窗口句柄（由主程序回填）
    uint64_t      launchTick;    // 启动器 GetTickCount64()
    uint64_t      readyTick;     // 主程序就绪 GetTickCount64()
    uint32_t      waitedMs;      // 启动器已等待毫秒（实时刷新）
    int32_t       silentStart;   // 静默启动标志
    int32_t       reserved0;     // 对齐保留
    wchar_t       message[VP_MAX_MESSAGE]; // UTF-16 状态文本
};

#pragma pack(pop)

static_assert(sizeof(VP_SHARED_HEADER) <= VP_HEADER_SIZE, "头部超出预留空间");

// 握手状态
enum VP_BOOT_STATE : LONG
{
    VP_CREATED      = 0,
    VP_LAUNCHING    = 1,
    VP_APP_ATTACHED = 2,
    VP_APP_READY    = 3,
    VP_FAILED       = 0x7FFFFFFF
};

// ── 握手通道：一对命名内核对象 ──────────────────────────────────────────────
//    * 共享内存：双方映射到同一块物理页，真正做到"同一内存交换数据"
//    * 就绪事件：主程序 SetEvent → 启动器立刻自杀
//    两者均带 "Local\" 前缀，保证会话隔离（多用户 / 远程桌面下互不干扰）。
struct VP_CHANNEL
{
    HANDLE  hMapping = nullptr;   // CreateFileMappingW 句柄
    HANDLE  hReady   = nullptr;   // 就绪事件句柄
    LPVOID  pView    = nullptr;   // 映射视图
    VP_SHARED_HEADER* header = nullptr;

    wchar_t mappingName[192] = {};
    wchar_t readyName[192]   = {};

    // 创建通道（启动器侧）
    bool Create()
    {
        // 用 PID + 高精度 tick 组合出会话内唯一名字，避免引入 GUID API 与额外依赖
        swprintf_s(mappingName, L"Local\\UNSA.VP.Boot.%lu.%llu",
                   GetCurrentProcessId(), GetTickCount64());
        swprintf_s(readyName, L"Local\\UNSA.VP.Ready.%lu.%llu",
                   GetCurrentProcessId(), GetTickCount64());

        hMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                      0, VP_MAPPING_SIZE, mappingName);
        if (!hMapping) return false;

        pView = MapViewOfFile(hMapping, FILE_MAP_ALL_ACCESS, 0, 0, VP_MAPPING_SIZE);
        if (!pView) return false;

        hReady = CreateEventW(nullptr, TRUE /*手动重置，避免竞态*/, FALSE, readyName);
        if (!hReady) return false;

        header = reinterpret_cast<VP_SHARED_HEADER*>(pView);
        header->magic        = VP_MAGIC;
        header->version      = VP_PROTOCOL_VERSION;
        header->state        = VP_CREATED;
        header->launcherPid  = GetCurrentProcessId();
        header->launchTick   = GetTickCount64();
        header->waitedMs     = 0;
        header->silentStart  = 0;
        wcscpy_s(header->message, L"正在启动…");
        return true;
    }

    // 读取主程序回填的状态
    VP_BOOT_STATE State() const
    {
        return header ? static_cast<VP_BOOT_STATE>(header->state) : VP_FAILED;
    }

    // 释放全部句柄 —— 最后一个句柄关闭时内核自动销毁命名对象，临时数据零残留
    void Close()
    {
        if (pView)    { UnmapViewOfFile(pView); pView = nullptr; header = nullptr; }
        if (hMapping) { CloseHandle(hMapping);  hMapping = nullptr; }
        if (hReady)   { CloseHandle(hReady);    hReady = nullptr; }
    }
};
