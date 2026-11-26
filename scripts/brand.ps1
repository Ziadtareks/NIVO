$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$nivoIconPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets/nivo.ico'
$nivoBitmap = New-Object System.Drawing.Bitmap 64,64
$nivoGraphics = [System.Drawing.Graphics]::FromImage($nivoBitmap)
$nivoGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$nivoGraphics.Clear([System.Drawing.Color]::Transparent)
$nivoRect = New-Object System.Drawing.Rectangle 2,2,60,60
$nivoBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $nivoRect,([System.Drawing.ColorTranslator]::FromHtml('#78C6FF')),([System.Drawing.ColorTranslator]::FromHtml('#1669CC')),65
$nivoGraphics.FillEllipse($nivoBrush,$nivoRect)
$nivoPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),4
$nivoPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$nivoPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$nivoPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$nivoPath.AddLine(20,44,20,23)
$nivoPath.AddBezier(20,23,20,16,25,17,29,24)
$nivoPath.AddLine(29,24,44,44)
$nivoPath.AddLine(44,44,44,20)
$nivoGraphics.DrawPath($nivoPen,$nivoPath)
$nivoHandle = $nivoBitmap.GetHicon()
$nivoIcon = [System.Drawing.Icon]::FromHandle($nivoHandle)
$nivoStream = [System.IO.File]::Create($nivoIconPath)
try { $nivoIcon.Save($nivoStream) } finally { $nivoStream.Dispose(); $nivoIcon.Dispose(); $nivoPath.Dispose(); $nivoPen.Dispose(); $nivoBrush.Dispose(); $nivoGraphics.Dispose(); $nivoBitmap.Dispose() }
