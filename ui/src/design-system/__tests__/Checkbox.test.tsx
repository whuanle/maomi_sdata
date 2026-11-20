import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it } from 'vitest';
import { Checkbox } from '../components/Checkbox';

it('consumes legacy children instead of forwarding them to a void input', () => {
  render(<Checkbox>Legacy label</Checkbox>);
  const control = screen.getByRole('checkbox', { name: 'Legacy label' });
  expect(control.childNodes).toHaveLength(0);
  fireEvent.click(control);
  expect(control).toBeChecked();
});
it('prefers explicit labels without forwarding children to the input', () => {
  render(<Checkbox label="Explicit label">Fallback</Checkbox>);
  expect(screen.getByRole('checkbox', { name: 'Explicit label' })).toBeInTheDocument();
  expect(screen.queryByText('Fallback')).not.toBeInTheDocument();
});
