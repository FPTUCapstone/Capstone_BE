import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('demo', Path(__file__).resolve().parents[2]/'tools'/'semantic-poc'/'demo.py')
demo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(demo)

class ComparatorTests(unittest.TestCase):
    def test_cleanup_failure_is_visible_and_not_success(self):
        from unittest.mock import patch
        result = {'status':'measured'}
        with patch.object(demo, 'http', side_effect=OSError('unavailable')):
            demo.cleanup_collection('http://localhost', 'tm70_poc_test', result)
        self.assertEqual(result['cleanup'], 'failed')
        self.assertIn('OSError', result['error'])

    def test_accent_insensitive_all_token_and_title_before_destination(self):
        tours = [
            {'id': 1, 'title': 'Biển xanh', 'destinations':['Đà Nẵng'], 'operator':'Op'},
            {'id': 2, 'title': 'Đà Nẵng', 'destinations':['Huế'], 'operator':'Op'},
            {'id': 3, 'title': 'Đà Lạt', 'destinations':['Huế'], 'operator':'Op'},
        ]
        self.assertEqual(demo.lexical('da nang', tours), [2, 1])
        self.assertEqual(demo.lexical('da missing', tours), [])

if __name__ == '__main__': unittest.main()
