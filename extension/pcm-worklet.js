// Videonun çaldığı sesi 16 kHz mono'ya indirger ve ~100 ms'lik paketler halinde ana betiğe yollar.
class PcmCapture extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.target = (options.processorOptions && options.processorOptions.targetRate) || 16000;
    this.ratio = sampleRate / this.target; // ör. 48000 / 16000 = 3
    this.pos = 0;                          // bir sonraki çıkış örneğinin giriş konumu (kesirli)
    this.acc = 0;                          // ortalama için toplam
    this.accN = 0;
    this.out = new Float32Array(this.target / 10);
    this.outN = 0;
    this.inIndex = 0;                      // toplam işlenen giriş örneği
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0) return true;
    const chans = input.length;
    const n = input[0].length;

    for (let i = 0; i < n; i++) {
      let v = 0;
      for (let c = 0; c < chans; c++) v += input[c][i];
      v /= chans;

      // Kutu filtresi: bir çıkış örneğine düşen giriş örneklerinin ortalaması (basit kenar yumuşatma)
      this.acc += v;
      this.accN++;
      this.inIndex++;
      if (this.inIndex >= this.pos + this.ratio) {
        this.pos += this.ratio;
        this.out[this.outN++] = this.acc / this.accN;
        this.acc = 0;
        this.accN = 0;
        if (this.outN === this.out.length) {
          this.port.postMessage(this.out, [this.out.buffer]);
          this.out = new Float32Array(this.target / 10);
          this.outN = 0;
        }
      }
    }
    return true;
  }
}

registerProcessor("pcm-capture", PcmCapture);
