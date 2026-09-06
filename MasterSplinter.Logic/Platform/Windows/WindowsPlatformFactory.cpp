// WindowsPlatformFactory.cpp — concrete Abstract Factory (Windows).
//
// Only news up the Windows process runner, so it needs no Windows headers itself:
// compiled with PrecompiledHeader=NotUsing.

#include "WindowsPlatformFactory.h"
#include "WindowsProcessRunner.h"
#include "../../Render/Windows/D2DGraphRenderer.h"

namespace ms
{
    std::unique_ptr<IProcessRunner> WindowsPlatformFactory::CreateProcessRunner() const
    {
        return std::make_unique<WindowsProcessRunner>();
    }

    std::unique_ptr<render::IGraphRenderer> WindowsPlatformFactory::CreateGraphRenderer() const
    {
        return render::CreateD2DGraphRenderer();
    }
}
