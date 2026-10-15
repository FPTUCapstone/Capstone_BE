import unittest
import math
from protocol import passage_text, fingerprint, prefixed, metrics

class ProtocolTests(unittest.TestCase):
    def test_public_content_canonical_and_fresh(self):
        self.assertEqual(passage_text('Tour', ['Huế', 'Đà Nẵng'], 'Op'), passage_text('Tour', ['Đà Nẵng', 'Huế'], 'Op'))
        self.assertNotEqual(fingerprint('Tour', ['Huế'], 'Op'), fingerprint('Changed', ['Huế'], 'Op'))
        self.assertEqual(prefixed(' biển ', 'query'), 'query: biển')
        with self.assertRaises(ValueError): prefixed(' ', 'query')
        with self.assertRaises(ValueError): prefixed('text', 'wrong')

    def test_cross_runtime_unicode_golden(self):
        self.assertEqual(fingerprint('Tour 😀\u2028a\u0301\n', ['😀', '\ue000', 'Đà Nẵng', 'Đà Nẵng'], '<Op>&\t"'), 'a131db8d80d13dcb3245281124e62e3390b5263a6ebc2eaa4ee2ae406811c6eb')

    def test_metrics_known_answer_and_empty_relevance(self):
        result = metrics([1, 2, 3], {1: 2, 3: 1})
        self.assertEqual(result['recall@5'], 1)
        self.assertEqual(result['precision@5'], 0.4)
        self.assertAlmostEqual(result['ndcg@10'], (3+1/math.log2(4))/(3+1/math.log2(3)))
        self.assertIsNone(metrics([1], {})['recall@5'])

if __name__ == '__main__': unittest.main()
