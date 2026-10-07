"""Developer-only fixture/reference generation. Executes official streaming definitions verbatim."""
import ast
import hashlib
import json
import wave
from pathlib import Path
import numpy as np
import onnxruntime as ort

ROOT = Path(__file__).resolve().parent.parent
source = ROOT / 'tests/official_streaming.py'
names = {'load_initial_state_from_metadata', 'validate_state_shape', 'vorbis_window',
         'STFTStreamingPreprocess', 'ISTFTStreamingPostprocess'}
tree = ast.parse(source.read_text(encoding='utf-8'))
tree.body = [n for n in tree.body if isinstance(n, (ast.FunctionDef, ast.ClassDef)) and n.name in names]
ns = {'np': np, 'ort': ort}
exec(compile(tree, str(source), 'exec'), ns)
opts = ort.SessionOptions()
opts.intra_op_num_threads = opts.inter_op_num_threads = 1
opts.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
opts.add_session_config_entry('session.intra_op.allow_spinning', '0')
opts.add_session_config_entry('session.inter_op.allow_spinning', '0')
session = ort.InferenceSession(str(ROOT/'models/dpdfnet2_48khz_hr.onnx'), opts, providers=['CPUExecutionProvider'])
state = ns['load_initial_state_from_metadata'](session)
ns['validate_state_shape'](session, state)
with wave.open(str(ROOT/'tests/speech.wav')) as f:
    voice = np.frombuffer(f.readframes(f.getnframes()), '<i2').astype(np.float32)/32768
    rate = f.getframerate()
voice = np.interp(np.arange(96000)/48000, np.arange(len(voice))/rate, voice, left=0, right=0).astype(np.float32)
voice *= .25/max(float(np.max(np.abs(voice))), 1e-6)
rng = np.random.default_rng(32)
x = np.concatenate([np.zeros(4800), voice + rng.normal(0,.008,len(voice)), rng.normal(0,.008,9600), np.zeros(4800)]).astype(np.float32)
x = np.pad(x,(0,(-len(x))%480))
window = ns['vorbis_window'](960)
stft = ns['STFTStreamingPreprocess'](960,480,window)
istft = ns['ISTFTStreamingPostprocess'](960,480,window)
identity_post = ns['ISTFTStreamingPostprocess'](960,480,window)
y = np.zeros_like(x); identity = np.zeros_like(x)
for pos in range(0,len(x),480):
    spec=stft(x[pos:pos+480])
    enhanced,state=session.run(None, {'spec':spec,'state_in':state})
    y[pos:pos+480]=istft(enhanced)
    identity[pos:pos+480]=identity_post(spec)
# Correlation corroborates fixed source alignment on known synthesized speech, not impulse alone.
scores = [float(np.dot(voice[:len(voice)-d], y[4800+d:4800+len(voice)])) for d in range(0,3841,480)]
delay = int(np.argmax(scores))*480
assert np.max(np.abs(identity[480:]-x[:-480])) < 2e-6
assert delay > 0, scores
x.tofile(ROOT/'tests/fixture.f32');y.tofile(ROOT/'tests/reference.f32')
model = ROOT/'models/dpdfnet2_48khz_hr.onnx'
sha = hashlib.sha256(model.read_bytes()).hexdigest()
profile = dict(model='dpdfnet2_48khz_hr', revision='dd6818d00f50c836fed43a6243ebe49116de5964',
    sha256=sha,bytes=model.stat().st_size,license='Apache-2.0',ort='1.23.2',
    official_reference_revision='9bd9844a227bb6aa57e55588d8d0e961fcff1c46',
    inputs=[dict(name=i.name,shape=i.shape,dtype=i.type) for i in session.get_inputs()],
    outputs=[dict(name=i.name,shape=i.shape,dtype=i.type) for i in session.get_outputs()],
    metadata=session.get_modelmeta().custom_metadata_map,
    alignment_samples=delay,availability_hops=delay//480+1,reserve_ms=20,
    correlation_by_hop=scores, identity_alignment_samples=480,
    fixture='Windows SAPI synthesized speech, silence, seeded noise. No microphone recording.',
    fixture_sha256=hashlib.sha256(x.tobytes()).hexdigest(),
    numerical_tolerance='max abs <= 2e-5, RMS <= 2e-6; float32 KissFFT vs numpy FFT roundoff, same ORT')
(ROOT/'models/profile.json').write_text(json.dumps(profile,indent=2)+'\n',encoding='utf-8')
(ROOT/'Native/model_hash.h').write_text(f'#pragma once\n#define DOTMIC_MODEL_SHA256 "{sha}"\n#define DOTMIC_ALIGNMENT_SAMPLES {delay}\n#define DOTMIC_AVAILABILITY_HOPS {delay//480+1}\n',encoding='utf-8')
print(json.dumps({'alignment_samples':delay,'correlation_by_hop':scores,'fixture_samples':len(x),'model_sha256':sha}))
