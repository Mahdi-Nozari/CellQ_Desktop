param([string]$Source, [string]$Output)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$projectDirectory = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $projectDirectory 'src\CellQ.Desktop\Assets\CellQ.svg' }
if (-not $Output) { $Output = Join-Path $projectDirectory 'src\CellQ.Desktop\Assets\CellQ.ico' }
# This deliberately small renderer supports the rect/path elements in our editable SVG.
# Other artwork can instead be exported to a multi-size .ico in any icon editor.
[xml]$svg = Get-Content -LiteralPath $Source -Raw -Encoding UTF8
$converter = New-Object Windows.Media.BrushConverter
$visual = New-Object Windows.Media.DrawingVisual
$drawing = $visual.RenderOpen()
try {
    foreach ($element in $svg.DocumentElement.ChildNodes) {
        if ($element.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
        $fill = if ($element.fill -and $element.fill -ne 'none') { $converter.ConvertFromString($element.fill) } else { $null }
        $pen = if ($element.stroke) { New-Object Windows.Media.Pen ($converter.ConvertFromString($element.stroke), [double]$element.'stroke-width') } else { $null }
        if ($pen) { $pen.LineJoin = [Windows.Media.PenLineJoin]::Round }
        switch ($element.LocalName) {
            'rect' { $drawing.DrawRoundedRectangle($fill, $pen, (New-Object Windows.Rect ([double]$element.x, [double]$element.y, [double]$element.width, [double]$element.height)), [double]$element.rx, [double]$element.rx) }
            'path' { $drawing.DrawGeometry($fill, $pen, [Windows.Media.Geometry]::Parse($element.d)) }
            default { throw "Unsupported icon element: $($element.LocalName). Use rect/path or export your own ICO." }
        }
    }
} finally { $drawing.Close() }
$frames = @()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $scaled = New-Object Windows.Media.DrawingVisual
    $context = $scaled.RenderOpen()
    try {
        $scale = $size / 256.0
        $context.PushTransform((New-Object Windows.Media.ScaleTransform ($scale, $scale)))
        $context.DrawDrawing($visual.Drawing)
        $context.Pop()
    } finally { $context.Close() }
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap ($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($scaled)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    try { $encoder.Save($stream); $frames += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() } } finally { $stream.Dispose() }
}
$file = [IO.File]::Create([IO.Path]::GetFullPath($Output))
$writer = New-Object IO.BinaryWriter ($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    [uint32]$offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write($offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
Write-Host "App icon: $Output"
