from inspect_ai import Task, task
from inspect_ai.dataset import Sample
from inspect_ai.scorer import match
from inspect_ai.solver import generate, system_message

@task
def tiny():
    return Task(
        dataset=[
            Sample(input="What is 2+2?", target="4", id=1, metadata={"difficulty": "easy"}),
            Sample(input="Name a colour.", target=["red", "blue"], id="colour"),
        ],
        solver=[system_message("Answer briefly."), generate()],
        scorer=match(),
        epochs=2,
    )
