# Local payment provider switch

User requested local payOS checkout return to Mock after successful real-payment testing. Existing Payment__Provider=Mock must also select the mock gateway for OperatorDirect attempts; keep Ready-channel onboarding gates and store the resolved gateway on new attempts. No real provider request or dummy transfer is needed. Existing real transactions are not rewritten and cannot be completed by the mock-outcome endpoint. Old pending real checkout links remain real; create a fresh booking for mock tests. No production configuration changes.
