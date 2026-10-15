import unittest
from pydantic import ValidationError
from app import Request

class RequestTests(unittest.TestCase):
    def test_empty_normalized_text_is_validation_error(self):
        with self.assertRaises(ValidationError):
            Request(text='   ', kind='query')

if __name__ == '__main__': unittest.main()
