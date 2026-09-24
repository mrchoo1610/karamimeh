namespace ProcessLoopbackSpike.Web;

/// <summary>
/// Trang HTML nội tuyến (nạp qua CoreWebView2.NavigateToString) — tạo 1 AudioContext +
/// OscillatorNode (sine) làm nguồn âm thanh xác định (deterministic) để test capture, thay vì
/// dùng video YouTube thật (không phụ thuộc mạng, không vướng autoplay policy — tone chỉ bắt đầu
/// khi người dùng bấm nút thật sự trong trang, đúng yêu cầu "user gesture" của Web Audio API).
/// </summary>
internal static class TestTonePage
{
    public const string Html = """
        <!DOCTYPE html>
        <html lang="vi">
        <head>
        <meta charset="utf-8" />
        <title>Test Tone</title>
        <style>
            html, body {
                margin: 0;
                padding: 0;
                height: 100%;
                background: #101018;
                color: #f0f0f5;
                font-family: 'Segoe UI', sans-serif;
                display: flex;
                flex-direction: column;
                align-items: center;
                justify-content: center;
                gap: 24px;
            }
            h1 { font-size: 20px; font-weight: 600; margin: 0 0 8px 0; color: #9c97b8; }
            .tone-row { display: flex; gap: 16px; }
            button.tone {
                font-size: 22px;
                padding: 20px 32px;
                border-radius: 12px;
                border: 2px solid #444;
                background: #23233a;
                color: #f0f0f5;
                cursor: pointer;
                min-width: 220px;
            }
            button.tone.active-440 { background: #1f5c3d; border-color: #3ddc97; }
            button.tone.active-880 { background: #1f3d5c; border-color: #22d3ee; }
            #status {
                font-size: 32px;
                font-weight: 700;
            }
            #status.playing { color: #3ddc97; }
            #status.stopped { color: #ff5a5f; }
            #hz { font-size: 14px; color: #9c97b8; }
        </style>
        </head>
        <body>
            <h1>karamimeh — ProcessLoopbackSpike: nguồn âm thử nghiệm</h1>
            <div id="status" class="stopped">ĐÃ DỪNG</div>
            <div id="hz">(chưa phát)</div>
            <div class="tone-row">
                <button class="tone" id="btn440">&#9654; Phát tone 440Hz</button>
                <button class="tone" id="btn880">&#9654; Phát tone 880Hz (phân biệt)</button>
            </div>
            <div class="tone-row">
                <button class="tone" id="btnStop">&#9632; Dừng</button>
            </div>

            <script>
                let ctx = null;
                let oscillator = null;
                let currentHz = null;

                function setStatus(playing, hz) {
                    const statusEl = document.getElementById('status');
                    const hzEl = document.getElementById('hz');
                    const btn440 = document.getElementById('btn440');
                    const btn880 = document.getElementById('btn880');

                    btn440.classList.remove('active-440');
                    btn880.classList.remove('active-880');

                    if (playing) {
                        statusEl.textContent = 'ĐANG PHÁT';
                        statusEl.className = 'playing';
                        hzEl.textContent = hz + ' Hz sine';
                        if (hz === 440) btn440.classList.add('active-440');
                        if (hz === 880) btn880.classList.add('active-880');
                    } else {
                        statusEl.textContent = 'ĐÃ DỪNG';
                        statusEl.className = 'stopped';
                        hzEl.textContent = '(đã dừng)';
                    }
                }

                function stopTone() {
                    if (oscillator) {
                        try { oscillator.stop(); } catch (e) {}
                        oscillator.disconnect();
                        oscillator = null;
                    }
                    currentHz = null;
                    setStatus(false, null);
                }

                function playTone(hz) {
                    // Yêu cầu user-gesture: hàm này chỉ được gọi từ 1 click handler thật, nên
                    // AudioContext luôn ở trạng thái 'running' ngay sau khi tạo (không bị Chromium
                    // autoplay-policy chặn).
                    stopTone();

                    if (!ctx) {
                        ctx = new (window.AudioContext || window.webkitAudioContext)();
                    }

                    oscillator = ctx.createOscillator();
                    oscillator.type = 'sine';
                    oscillator.frequency.value = hz;
                    oscillator.connect(ctx.destination);
                    oscillator.start();
                    currentHz = hz;
                    setStatus(true, hz);
                }

                document.getElementById('btn440').addEventListener('click', () => playTone(440));
                document.getElementById('btn880').addEventListener('click', () => playTone(880));
                document.getElementById('btnStop').addEventListener('click', () => stopTone());
            </script>
        </body>
        </html>
        """;
}
