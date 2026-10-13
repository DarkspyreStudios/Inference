"""Prepare reference token IDs and retrieval inputs for the production Gemma tester."""
import argparse
import json
import shutil
from pathlib import Path
from tokenizers import Tokenizer

parser = argparse.ArgumentParser()
parser.add_argument('model', type=Path)
parser.add_argument('images', type=Path)
parser.add_argument('output', type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
tokenizer = Tokenizer.from_file(str(args.model / 'tokenizer.json'))
cases = [
    dict(name='lifetime', kind='document', title='Model ownership', text='Unload must wait for active inference to finish before releasing the model and its native runtime.'),
    dict(name='christmas', kind='document', title='Family holiday', text='Christmas dinner is at our house on December 25. Bring the red ornaments and gifts for the children.'),
    dict(name='database', kind='document', title='Schema changes', text='Entity Framework generates database migrations. The service applies pending migrations on startup.'),
    dict(name='code', kind='document', title='Source code', text='public Task UnloadAsync(CancellationToken token) { return DrainAndReleaseAsync(token); }'),
    dict(name='lifetime-query', kind='query', text='When can we release the native model safely?'),
    dict(name='christmas-query', kind='query', text='anything about christmas'),
    dict(name='database-query', kind='query', text='How is the initial database created and updated?'),
]
texts = ['', 'hello', 'hello world', ' a  b   ', 'line one\nline two\n\nline four', 'tab\tend\r\nnew',
         'public class Widget {\n    int Count => 42;\n}', 'café Noël 中文 日本語 مرحبا', '😀 🎄 🎁',
         '<bos>hello<eos>', '1.2e-3 foo_bar/path.js?!', 'first\u00a0second\u2003third']
for item in cases:
    texts.append(('task: search result | query: ' + item['text']) if item['kind'] == 'query'
                 else ('title: ' + item.get('title', 'none') + ' | text: ' + item['text']))
for name in ['square', 'portrait', 'landscape']:
    shutil.copyfile(args.images / (name + '.png'), args.output / (name + '.png'))
    cases.append(dict(name=name, kind='document', image=name + '.png'))
manifest = dict(tokenizer=[dict(text=text, ids=tokenizer.encode(text).ids) for text in texts], cases=cases,
                retrieval=[dict(query=name + '-query', expected=name, documents=['lifetime','christmas','database','code'])
                           for name in ['lifetime','christmas','database']])
(args.output / 'engine.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2))
print(f'Prepared {len(texts)} tokenizer cases, {len(cases)} embedding cases and 3 retrieval checks.')
