// Windows renderer. Includes <windows.h> via the D3D/D2D headers -- see the header for why that
// is allowed here and nowhere else outside Platform/Windows.

#include "D2DGraphRenderer.h"

#include "../../Graph/GraphLayout.h"

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <d2d1_1.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <set>
#include <string>
#include <vector>

namespace ms::render
{
    namespace
    {
        template <typename T>
        using ComPtr = Microsoft::WRL::ComPtr<T>;

        // ISwapChainPanelNative, declared by hand rather than pulled from
        // microsoft.ui.xaml.media.dxinterop.h.
        //
        // That header exists (it ships in the Windows App SDK WinUI package) but reaching it would
        // mean adding the whole WinUI NuGet package to a plain C++ DLL that otherwise needs
        // nothing from it. The interface is one method and a GUID; the GUID is the contract, and
        // it is stable.
        MIDL_INTERFACE("63aad0b8-7c24-40ff-85a8-640d944cc325")
        ISwapChainPanelNative : public IUnknown
        {
        public:
            virtual HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain * swapChain) = 0;
        };

        // Matches the host's GraphColor enum, which the layout's colour indices cycle through.
        // Deliberately the same six values GraphCanvas used, so the switch to Direct2D changes
        // nothing a user can see except how it is drawn.
        constexpr D2D1_COLOR_F kPalette[] = {
            { 0.184f, 0.435f, 0.922f, 1.0f },   // Blue    #2F6FEB
            { 0.247f, 0.725f, 0.314f, 1.0f },   // Green   #3FB950
            { 0.859f, 0.549f, 0.157f, 1.0f },   // Orange  #DB8C28
            { 0.898f, 0.325f, 0.294f, 1.0f },   // Red     #E5534B
            { 0.545f, 0.580f, 0.620f, 1.0f },   // Gray    #8B949E
            { 0.639f, 0.443f, 0.969f, 1.0f },   // Purple  #A371F7
        };
        constexpr std::size_t kPaletteSize = sizeof(kPalette) / sizeof(kPalette[0]);

        // Geometry, matching what GraphCanvas drew so the two are interchangeable during the
        // transition.
        constexpr float kLaneWidth = 14.0f;
        constexpr float kLeftPad = 8.0f;
        constexpr float kDotRadius = 4.0f;
        constexpr float kLineThickness = 2.0f;

        // The ListViewItem's own selection band, measured off a screenshot: inset 2px vertically,
        // rounded at the corners. Reproduced so the two halves of one row read as one band.
        constexpr float kSelectionInset = 2.0f;
        constexpr float kSelectionRadius = 4.0f;

        float LaneX(int lane)
        {
            return kLeftPad + (lane * kLaneWidth) + (kLaneWidth / 2.0f);
        }

        D2D1_COLOR_F FromArgb(std::uint32_t argb)
        {
            return D2D1::ColorF(argb & 0x00FFFFFFu, ((argb >> 24) & 0xFFu) / 255.0f);
        }

        class D2DGraphRenderer final : public IGraphRenderer
        {
        public:
            ~D2DGraphRenderer() override = default;

            bool Attach(void* surface) override
            {
                panel_.Reset();
                if (!surface)
                    return false;

                ComPtr<IUnknown> unknown(static_cast<IUnknown*>(surface));
                if (FAILED(unknown.As(&panel_)))
                    return false;

                return CreateDevice();
            }

            void SetModel(const void* displayList, int length) override
            {
                model_.clear();
                if (displayList && length > 0)
                    model_.assign(static_cast<const char*>(displayList),
                                  static_cast<const char*>(displayList) + length);
                rowOffsets_.clear();
                IndexRows();
            }

            void SetChrome(const Chrome& chrome) override { chrome_ = chrome; }

            void SetSelection(const int* rows, int count) override
            {
                selection_.clear();
                for (int i = 0; i < count && rows; ++i)
                    selection_.insert(rows[i]);
            }

            void SetViewport(const Viewport& viewport) override
            {
                const bool resized = viewport.width != viewport_.width ||
                                     viewport.height != viewport_.height ||
                                     viewport.scale != viewport_.scale;
                viewport_ = viewport;
                if (resized)
                    ResizeBuffers();
            }

            void Render() override
            {
                if (!context_ || !swapChain_)
                    return;

                context_->BeginDraw();
                // Cleared to the list background, not to transparent: see Chrome in
                // IGraphRenderer.h -- a WinUI 3 SwapChainPanel does not blend with the XAML
                // content behind it, so a transparent clear leaves black rather than the rows.
                context_->Clear(FromArgb(chrome_.background));

                DrawVisibleRows();

                const HRESULT drawn = context_->EndDraw();
                const HRESULT shown = swapChain_->Present(1, 0);

                // A device can be lost at any time -- a driver update, a GPU reset, an RDP
                // reconnect. Rebuild rather than leaving a dead surface on screen.
                if (drawn == D2DERR_RECREATE_TARGET ||
                    shown == DXGI_ERROR_DEVICE_REMOVED || shown == DXGI_ERROR_DEVICE_RESET)
                {
                    ReleaseDevice();
                    CreateDevice();
                }
            }

        private:
            // ---- Display list ---------------------------------------------------------------

            std::uint8_t Byte(std::size_t at) const
            {
                return at < model_.size() ? static_cast<std::uint8_t>(model_[at]) : 0;
            }

            // Records where each row's header starts, so a scroll can jump straight to the first
            // visible row instead of walking the list from the top every frame.
            void IndexRows()
            {
                if (model_.size() < 4)
                    return;

                const std::uint32_t rows = static_cast<std::uint32_t>(Byte(0)) |
                                           (static_cast<std::uint32_t>(Byte(1)) << 8) |
                                           (static_cast<std::uint32_t>(Byte(2)) << 16) |
                                           (static_cast<std::uint32_t>(Byte(3)) << 24);
                rowOffsets_.reserve(rows);

                std::size_t at = 4;
                for (std::uint32_t r = 0; r < rows; ++r)
                {
                    if (at + graph::kRowHeaderSize > model_.size())
                        break;
                    rowOffsets_.push_back(at);
                    const std::size_t segments = Byte(at + 4);
                    at += graph::kRowHeaderSize + (segments * graph::kSegmentSize);
                    if (at > model_.size())
                    {
                        rowOffsets_.pop_back();
                        break;
                    }
                }
            }

            const D2D1_COLOR_F& Color(std::uint8_t index) const
            {
                return kPalette[index % kPaletteSize];
            }

            void DrawVisibleRows()
            {
                if (rowOffsets_.empty() || viewport_.rowHeight <= 0.0f)
                    return;

                const float rowHeight = viewport_.rowHeight;
                const double first = std::floor(viewport_.scrollPx / rowHeight);
                const std::size_t begin = first < 0.0 ? 0 : static_cast<std::size_t>(first);
                const std::size_t span =
                    static_cast<std::size_t>(std::ceil(viewport_.height / rowHeight)) + 2;
                const std::size_t end = std::min(rowOffsets_.size(), begin + span);

                for (std::size_t row = begin; row < end; ++row)
                {
                    const std::size_t at = rowOffsets_[row];
                    const float top = static_cast<float>((row * static_cast<double>(rowHeight)) -
                                                         viewport_.scrollPx);
                    const float half = rowHeight / 2.0f;

                    // The selected row keeps its highlight across the graph column, so a selection
                    // reads as one band rather than stopping at the Description column.
                    if (selection_.find(static_cast<int>(row)) != selection_.end())
                    {
                        // Inset and rounded to match what the ListViewItem draws on its own half
                        // of the row -- measured: WinUI insets its selection band by 2px top and
                        // bottom, so a plain full-height rectangle here left a visible step at the
                        // column boundary. The band is the row's left end, so only that side is
                        // rounded; the right runs on under the Description column.
                        brush_->SetColor(FromArgb(chrome_.selected));
                        const D2D1_ROUNDED_RECT band = D2D1::RoundedRect(
                            D2D1::RectF(kSelectionInset, top + kSelectionInset,
                                        viewport_.width + kSelectionRadius,
                                        top + rowHeight - kSelectionInset),
                            kSelectionRadius, kSelectionRadius);
                        context_->FillRoundedRectangle(band, brush_.Get());
                    }

                    const int dotLane = Byte(at + 1);
                    const std::uint8_t dotColor = Byte(at + 2);
                    const std::uint8_t flags = Byte(at + 3);
                    const std::size_t segments = Byte(at + 4);

                    std::size_t seg = at + graph::kRowHeaderSize;
                    for (std::size_t s = 0; s < segments; ++s, seg += graph::kSegmentSize)
                    {
                        brush_->SetColor(Color(Byte(seg + 4)));
                        context_->DrawLine(
                            D2D1::Point2F(LaneX(Byte(seg)), top + (Byte(seg + 1) * half)),
                            D2D1::Point2F(LaneX(Byte(seg + 2)), top + (Byte(seg + 3) * half)),
                            brush_.Get(), kLineThickness);
                    }

                    const D2D1_ELLIPSE dot = D2D1::Ellipse(
                        D2D1::Point2F(LaneX(dotLane), top + half), kDotRadius, kDotRadius);
                    brush_->SetColor(Color(dotColor));
                    if (flags & graph::kFlagBoundary)
                    {
                        // Hollow: this commit's history continues past the loaded window, so the
                        // line leaving the bottom of the row is not a dead end.
                        context_->DrawEllipse(dot, brush_.Get(), 2.0f);
                    }
                    else
                    {
                        context_->FillEllipse(dot, brush_.Get());
                    }
                }
            }

            // ---- Device ---------------------------------------------------------------------

            bool CreateDevice()
            {
                if (!panel_)
                    return false;

                UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;   // required by Direct2D
                ComPtr<ID3D11Device> d3d;
                HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
                                               nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, nullptr);
                if (FAILED(hr))
                {
                    // No usable GPU (a VM, a stripped container). WARP is slower but correct, and
                    // a software-rendered graph beats no graph.
                    hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags,
                                           nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, nullptr);
                }
                if (FAILED(hr))
                    return false;

                ComPtr<IDXGIDevice1> dxgiDevice;
                if (FAILED(d3d.As(&dxgiDevice)))
                    return false;

                D2D1_FACTORY_OPTIONS options{};
                ComPtr<ID2D1Factory1> factory;
                if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,
                                             __uuidof(ID2D1Factory1), &options, &factory)))
                    return false;

                if (FAILED(factory->CreateDevice(dxgiDevice.Get(), &d2dDevice_)))
                    return false;
                if (FAILED(d2dDevice_->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context_)))
                    return false;
                if (FAILED(context_->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White), &brush_)))
                    return false;

                ComPtr<IDXGIAdapter> adapter;
                if (FAILED(dxgiDevice->GetAdapter(&adapter)))
                    return false;
                ComPtr<IDXGIFactory2> dxgiFactory;
                if (FAILED(adapter->GetParent(IID_PPV_ARGS(&dxgiFactory))))
                    return false;

                DXGI_SWAP_CHAIN_DESC1 desc{};
                desc.Width = 1;                 // resized to the real viewport below
                desc.Height = 1;
                desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                desc.SampleDesc.Count = 1;
                desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
                desc.BufferCount = 2;
                desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                // PREMULTIPLIED, not IGNORE: the panel sits on top of the history list, and the
                // row selection highlight has to show through the gaps between lanes.
                desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;

                if (FAILED(dxgiFactory->CreateSwapChainForComposition(d3d.Get(), &desc, nullptr,
                                                                      &swapChain_)))
                    return false;
                if (FAILED(panel_->SetSwapChain(swapChain_.Get())))
                    return false;

                d3d_ = d3d;
                ResizeBuffers();
                return true;
            }

            void ReleaseDevice()
            {
                if (context_)
                    context_->SetTarget(nullptr);
                if (panel_)
                    panel_->SetSwapChain(nullptr);
                brush_.Reset();
                context_.Reset();
                d2dDevice_.Reset();
                swapChain_.Reset();
                d3d_.Reset();
            }

            void ResizeBuffers()
            {
                if (!swapChain_ || !context_)
                    return;

                const UINT w = static_cast<UINT>(std::max(1.0f, viewport_.width * viewport_.scale));
                const UINT h = static_cast<UINT>(std::max(1.0f, viewport_.height * viewport_.scale));

                context_->SetTarget(nullptr);
                if (FAILED(swapChain_->ResizeBuffers(2, w, h, DXGI_FORMAT_B8G8R8A8_UNORM, 0)))
                    return;

                ComPtr<IDXGISurface> surface;
                if (FAILED(swapChain_->GetBuffer(0, IID_PPV_ARGS(&surface))))
                    return;

                // The bitmap is in physical pixels; setting the DPI to 96*scale lets everything
                // below draw in the same device-independent units the host lays out in.
                const D2D1_BITMAP_PROPERTIES1 props = D2D1::BitmapProperties1(
                    D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
                    D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
                    96.0f * viewport_.scale, 96.0f * viewport_.scale);

                ComPtr<ID2D1Bitmap1> target;
                if (FAILED(context_->CreateBitmapFromDxgiSurface(surface.Get(), &props, &target)))
                    return;
                context_->SetTarget(target.Get());
            }

            ComPtr<ISwapChainPanelNative> panel_;
            ComPtr<ID3D11Device> d3d_;
            ComPtr<ID2D1Device> d2dDevice_;
            ComPtr<ID2D1DeviceContext> context_;
            ComPtr<ID2D1SolidColorBrush> brush_;
            ComPtr<IDXGISwapChain1> swapChain_;

            Viewport viewport_{};
            Chrome chrome_{};
            std::set<int> selection_;
            std::string model_;
            std::vector<std::size_t> rowOffsets_;
        };
    }

    std::unique_ptr<IGraphRenderer> CreateD2DGraphRenderer()
    {
        return std::make_unique<D2DGraphRenderer>();
    }
}
